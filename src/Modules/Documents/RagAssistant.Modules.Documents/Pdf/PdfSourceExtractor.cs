using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Storage;

namespace RagAssistant.Modules.Documents.Pdf;

/// <summary>PDF versions (spec 010): reads the original from the file store and its text layer within <see cref="PdfOptions.ExtractionTimeout"/>.</summary>
internal sealed class PdfSourceExtractor(
    IPdfTextExtractor pdfExtractor,
    IOriginalFileStore store,
    IOptions<PdfOptions> pdfOptions,
    DocumentsMetrics metrics,
    TimeProvider clock) : ISourceExtractor
{
    public SourceFormat Format => SourceFormat.Pdf;

    public async Task<SourceExtraction> ExtractAsync(VersionSource source, Action<int, int>? onProgress, CancellationToken cancellationToken)
    {
        var key = source.StorageKey ?? throw new InvalidOperationException("The PDF version has no storage key.");
        await using var stream = store.OpenRead(key)
            ?? throw new IngestionException(IngestionErrorCodes.FileMissing, "The original file is missing from the store.");

        using var timeout = new CancellationTokenSource(pdfOptions.Value.ExtractionTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = clock.GetTimestamp();
        PdfExtraction pdf;
        var work = Task.Run(() => pdfExtractor.Extract(stream, onProgress, linked.Token), linked.Token);
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

        metrics.PdfExtracted(pdf.PageCount, clock.GetElapsedTime(started));
        return new SourceExtraction(
            ToDocument(pdf.Blocks),
            $"{pdf.PageCount} pages, {pdf.PagesWithoutText.Length} without text, {pdf.MarginLinesRemoved} margin lines removed",
            pdf.PagesWithoutText);
    }

    /// <summary>
    /// Consecutive paragraphs with the same heading chain form a section. PDF headings are not repeated in passage text, so sections
    /// have no heading line; every block keeps the page it was found on.
    /// </summary>
    public static SourceDocument ToDocument(IReadOnlyList<PdfBlock> blocks)
    {
        var sections = new List<(string[] Path, List<SourceBlock> Blocks)>();
        foreach (var block in blocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)))
        {
            if (sections.Count == 0 || !sections[^1].Path.AsSpan().SequenceEqual(block.SectionPath))
            {
                sections.Add((block.SectionPath, []));
            }

            sections[^1].Blocks.Add(new SourceBlock(BlockKind.Paragraph, block.Text.Split('\n'), block.Page));
        }

        return new SourceDocument([.. sections.Select(s => new SourceSection(s.Path, null, s.Blocks))]);
    }
}
