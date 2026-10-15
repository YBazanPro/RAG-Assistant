using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Pdf;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Modules.Documents.Storage;

namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>
/// Runs one ingestion run: extract, chunk, embed, store. Every stage transition is a committed <see cref="IngestionEvent"/>
/// except the last two, which commit together with the passages so a stream never announces completion before the data exists.
/// </summary>
internal sealed partial class IngestionRunProcessor(
    DocumentsDbContext db,
    PassageEmbedder embedder,
    IOptions<ChunkingOptions> chunking,
    IOptions<IngestionOptions> ingestion,
    IOptions<EmbeddingOptions> embeddingOptions,
    IOptions<PdfOptions> pdfOptions,
    IOriginalFileStore store,
    IPdfTextExtractor pdfExtractor,
    DocumentsMetrics metrics,
    TimeProvider clock,
    ILogger<IngestionRunProcessor> logger)
{
    private const string UnexpectedMessage = "Unexpected error; see logs";

    private sealed record VersionInput(Guid DocumentId, int Number, string? Content, string? StorageKey, SourceFormat Format);

    private sealed record Extracted(string? Text, PdfExtraction? Pdf);

    public async Task ProcessAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.IngestionRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.VersionId, r.Attempt })
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null || !await ClaimAsync(runId, run.VersionId, cancellationToken))
        {
            return;
        }

        var started = clock.GetTimestamp();
        // A root span per run: the ambient activity here belongs to worker startup and would put every run in one endless trace.
        // Clearing it is local to this async flow; an explicit default parent context still falls back to Activity.Current.
        Activity.Current = null;
        using var activity = DocumentsTelemetry.Source.StartActivity("ingestion.run");
        activity?.SetTag("run.id", runId);
        activity?.SetTag("version.id", run.VersionId);
        activity?.SetTag("run.attempt", run.Attempt);

        var stage = IngestionStage.Extracting;
        try
        {
            var version = await db.Versions.AsNoTracking()
                .Where(v => v.Id == run.VersionId)
                .Select(v => new VersionInput(v.DocumentId, v.Number, v.Content, v.StorageKey, v.SourceFormat))
                .SingleAsync(cancellationToken);

            var extracted = await RunStageAsync(runId, stage, () => ExtractAsync(version, runId, run.VersionId, cancellationToken), cancellationToken);

            stage = IngestionStage.Chunking;
            var passages = await RunStageAsync(
                runId,
                stage,
                () =>
                {
                    var chunks = extracted.Pdf is { } pdf
                        ? Chunker.Chunk(pdf.Blocks, chunking.Value)
                        : Chunker.Chunk(extracted.Text!, version.Format, chunking.Value);
                    return chunks.Count == 0
                        ? throw new IngestionException(IngestionErrorCodes.NoPassages, "The document has no indexable text.")
                        : (chunks, $"{chunks.Count} passages");
                },
                cancellationToken);

            stage = IngestionStage.Embedding;
            var vectors = await RunStageAsync(
                runId,
                stage,
                async () =>
                {
                    var batchSize = ingestion.Value.EmbeddingBatchSize;
                    var inputs = passages.Select(p => EmbeddingInput.Build(p.SectionPath, p.Text)).ToArray();
                    var tracker = new ProgressTracker(inputs.Length);
                    var result = await embedder.EmbedAsync(
                        inputs,
                        cancellationToken,
                        async done =>
                        {
                            if (tracker.ShouldReport(done))
                            {
                                await RecordProgressAsync(runId, IngestionStage.Embedding, done, inputs.Length, cancellationToken);
                            }
                        });
                    return (result, $"{(inputs.Length + batchSize - 1) / batchSize} batches");
                },
                cancellationToken);

            stage = IngestionStage.Storing;
            using (var storing = DocumentsTelemetry.Source.StartActivity("ingestion.stage"))
            {
                storing?.SetTag("stage", "storing");
                await RecordEventAsync(runId, stage, EventStatus.Started, null, cancellationToken);
                await StoreAsync(runId, run.VersionId, version.DocumentId, passages, vectors, cancellationToken);
            }

            var elapsed = clock.GetElapsedTime(started);
            metrics.IngestionCompleted(passages.Count, elapsed);
            LogCompleted(logger, runId, run.VersionId, run.Attempt, passages.Count, elapsed.TotalSeconds);
        }
        catch (IngestionException ex)
        {
            await FailAsync(runId, run.VersionId, run.Attempt, stage, ex.Code, ex.Message, clock.GetElapsedTime(started), activity, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Catch-all by design (spec 007, AC-17: an unexpected stage failure fails the run with a content-free message).
            // Cancellation is excluded on purpose: a stopping host writes nothing and the run is re-queued at the next start.
            LogUnexpected(logger, ex, runId, run.VersionId, stage);
            await FailAsync(
                runId, run.VersionId, run.Attempt, stage, IngestionErrorCodes.IngestionError, UnexpectedMessage,
                clock.GetElapsedTime(started), activity, cancellationToken);
        }
    }

    private async Task<(Extracted Result, string Summary)> ExtractAsync(VersionInput version, Guid runId, Guid versionId, CancellationToken cancellationToken)
    {
        if (version.Format != SourceFormat.Pdf)
        {
            var text = version.Content ?? throw new InvalidOperationException("The version has no text content.");
            return (new Extracted(text, null), $"{text.Length} characters");
        }

        var key = version.StorageKey ?? throw new InvalidOperationException("The PDF version has no storage key.");
        await using var stream = store.OpenRead(key)
            ?? throw new IngestionException(IngestionErrorCodes.FileMissing, "The original file is missing from the store.");

        using var timeout = new CancellationTokenSource(pdfOptions.Value.ExtractionTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        // The extractor runs on another thread; its page counts go through a channel so only this flow touches the DbContext.
        var pages = Channel.CreateUnbounded<(int Done, int Total)>();
        var writer = WriteExtractionProgressAsync(pages.Reader, runId, cancellationToken);
        var started = clock.GetTimestamp();
        PdfExtraction pdf;
        var work = Task.Run(() => pdfExtractor.Extract(stream, (done, total) => pages.Writer.TryWrite((done, total)), linked.Token), linked.Token);
        try
        {
            // The parser checks the token between pages only; waiting on the token as well frees the run even inside one slow call.
            pdf = await work.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            metrics.PdfExtractionFailed(clock.GetElapsedTime(started));
            throw new IngestionException(IngestionErrorCodes.ExtractionTimeout, "Extracting the text took too long.");
        }
        catch (IngestionException)
        {
            metrics.PdfExtractionFailed(clock.GetElapsedTime(started));
            throw;
        }
        finally
        {
            pages.Writer.TryComplete();
            await writer;
        }

        metrics.PdfExtracted(pdf.PageCount, clock.GetElapsedTime(started));

        int[]? withoutText = pdf.PagesWithoutText.Length == 0 ? null : pdf.PagesWithoutText;
        await db.Versions.Where(v => v.Id == versionId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.PagesWithoutText, withoutText), cancellationToken);
        return (new Extracted(null, pdf), $"{pdf.PageCount} pages, {pdf.PagesWithoutText.Length} without text, {pdf.MarginLinesRemoved} margin lines removed");
    }

    private async Task WriteExtractionProgressAsync(ChannelReader<(int Done, int Total)> reader, Guid runId, CancellationToken cancellationToken)
    {
        ProgressTracker? tracker = null;
        await foreach (var (done, total) in reader.ReadAllAsync(CancellationToken.None))
        {
            tracker ??= new ProgressTracker(total);
            if (tracker.ShouldReport(done))
            {
                await RecordProgressAsync(runId, IngestionStage.Extracting, done, total, cancellationToken);
            }
        }
    }

    private async Task RecordProgressAsync(Guid runId, IngestionStage stage, int current, int total, CancellationToken cancellationToken)
    {
        var progress = NewEvent(runId, stage, EventStatus.Progress, null);
        progress.Current = current;
        progress.Total = total;
        db.IngestionEvents.Add(progress);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ClaimAsync(Guid runId, Guid versionId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.IngestionRuns
            .Where(r => r.Id == runId && r.Status == RunStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Running).SetProperty(r => r.StartedAt, now), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        await db.Versions
            .Where(v => v.Id == versionId && v.Status == VersionStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatus.Processing), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<T> RunStageAsync<T>(Guid runId, IngestionStage stage, Func<(T Result, string Summary)> body, CancellationToken cancellationToken) =>
        await RunStageAsync(runId, stage, () => Task.FromResult(body()), cancellationToken);

    private async Task<T> RunStageAsync<T>(Guid runId, IngestionStage stage, Func<Task<(T Result, string Summary)>> body, CancellationToken cancellationToken)
    {
        using var activity = DocumentsTelemetry.Source.StartActivity("ingestion.stage");
        activity?.SetTag("stage", stage.ToString().ToLowerInvariant());

        await RecordEventAsync(runId, stage, EventStatus.Started, null, cancellationToken);
        await db.IngestionRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CurrentStage, stage), cancellationToken);

        var (result, summary) = await body();
        await RecordEventAsync(runId, stage, EventStatus.Completed, summary, cancellationToken);
        return result;
    }

    private async Task RecordEventAsync(Guid runId, IngestionStage stage, EventStatus status, string? message, CancellationToken cancellationToken)
    {
        db.IngestionEvents.Add(NewEvent(runId, stage, status, message));
        await db.SaveChangesAsync(cancellationToken);
    }

    private IngestionEvent NewEvent(Guid runId, IngestionStage stage, EventStatus status, string? message) => new()
    {
        RunId = runId,
        Stage = stage,
        Status = status,
        Message = message,
        OccurredAt = clock.GetUtcNow(),
    };

    private async Task StoreAsync(
        Guid runId,
        Guid versionId,
        Guid documentId,
        IReadOnlyList<ChunkedPassage> chunks,
        IReadOnlyList<float[]> vectors,
        CancellationToken cancellationToken)
    {
        var embedding = embeddingOptions.Value;
        var modelId = embedder.ModelId;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Passages.AddRange(chunks.Select((c, i) => new Passage
        {
            Id = Guid.CreateVersion7(),
            VersionId = versionId,
            Position = c.Position,
            SectionPath = c.SectionPath,
            SectionOrdinal = c.SectionOrdinal,
            Text = c.Text,
            PageStart = c.PageStart,
            PageEnd = c.PageEnd,
            CharCount = c.CharCount,
            TokenEstimate = c.TokenEstimate,
            Embedding = new Vector(vectors[i]),
        }));
        db.IngestionEvents.Add(NewEvent(runId, IngestionStage.Storing, EventStatus.Completed, $"{chunks.Count} passages"));
        db.IngestionEvents.Add(NewEvent(runId, IngestionStage.Completed, EventStatus.Completed, "Indexed"));
        await db.SaveChangesAsync(cancellationToken);

        var previousActive = await db.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.ActiveVersionId)
            .SingleAsync(cancellationToken);
        if (previousActive is { } previous && previous != versionId)
        {
            await db.Versions.Where(v => v.Id == previous)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatus.Superseded), cancellationToken);
        }

        var passageCount = chunks.Count;
        await db.Versions.Where(v => v.Id == versionId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(v => v.Status, VersionStatus.Indexed)
                    .SetProperty(v => v.PassageCount, passageCount)
                    .SetProperty(v => v.EmbeddingModel, modelId)
                    .SetProperty(v => v.EmbeddingDimensions, embedding.Dimensions)
                    .SetProperty(v => v.IndexedAt, now),
                cancellationToken);
        // Deliberately leaves Document.UpdatedAt alone: ingestion is the system's work, not an edit by a user.
        await db.Documents.Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ActiveVersionId, versionId), cancellationToken);
        await db.IngestionRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, RunStatus.Succeeded)
                    .SetProperty(r => r.CurrentStage, IngestionStage.Completed)
                    .SetProperty(r => r.FinishedAt, now),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private async Task FailAsync(
        Guid runId,
        Guid versionId,
        int attempt,
        IngestionStage stage,
        string code,
        string message,
        TimeSpan elapsed,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.IngestionEvents.Add(NewEvent(runId, stage, EventStatus.Failed, message));
        await db.SaveChangesAsync(cancellationToken);
        await db.IngestionRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, RunStatus.Failed)
                    .SetProperty(r => r.CurrentStage, stage)
                    .SetProperty(r => r.FinishedAt, now)
                    .SetProperty(r => r.ErrorCode, code)
                    .SetProperty(r => r.ErrorMessage, message),
                cancellationToken);
        await db.Versions.Where(v => v.Id == versionId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatus.Failed), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetStatus(ActivityStatusCode.Error, code);
        metrics.IngestionFailed(code, elapsed);
        LogFailed(logger, runId, versionId, attempt, stage, code);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ingestion run {RunId} indexed version {VersionId} (attempt {Attempt}): {PassageCount} passages in {Seconds:F1}s")]
    private static partial void LogCompleted(ILogger logger, Guid runId, Guid versionId, int attempt, int passageCount, double seconds);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ingestion run {RunId} for version {VersionId} failed (attempt {Attempt}) in stage {Stage}: {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, Guid runId, Guid versionId, int attempt, IngestionStage stage, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion run {RunId} for version {VersionId} hit an unexpected error in stage {Stage}")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, Guid runId, Guid versionId, IngestionStage stage);
}
