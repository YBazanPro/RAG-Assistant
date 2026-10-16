using System.Data.Common;
using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Features.AskQuestion;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>
/// The fixed answer pipeline of ADR 0001 and spec 012: retrieve, refuse without a model call or generate a streamed answer,
/// validate its citations, then persist the answer, its citation snapshots and the request trace in one transaction.
/// A producer task does the work and writes events into a channel; the stream reads the channel and adds heartbeats while
/// nothing else is sent.
/// </summary>
internal sealed partial class AnswerPipeline(
    ChatDbContext db,
    IDocumentSearch search,
    IDocumentCatalog catalog,
    IChatClient chat,
    IOptions<GenerationOptions> generationOptions,
    IOptions<ChatRetrievalOptions> retrievalOptions,
    IOptions<FollowUpOptions> followUpOptions,
    TimeProvider clock,
    ILogger<AnswerPipeline> logger)
{
    /// <summary>The disconnect cancels the request token, but the cancelled answer and its trace must still be written.</summary>
    private static readonly TimeSpan FinalWriteTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions TraceJson = new(JsonSerializerDefaults.Web);

    private GenerationOptions Generation => generationOptions.Value;

    public async IAsyncEnumerable<SseItem<string>> RunAsync(AcceptedAnswer answer, [EnumeratorCancellation] CancellationToken requestAborted)
    {
        var channel = Channel.CreateUnbounded<SseItem<string>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(answer, channel.Writer, requestAborted);
        try
        {
            var reader = channel.Reader;
            while (true)
            {
                var ready = reader.WaitToReadAsync(requestAborted).AsTask();
                while (!ready.IsCompleted)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
                    var winner = await Task.WhenAny(ready, Task.Delay(Generation.HeartbeatInterval, clock, idle.Token));
                    await idle.CancelAsync();
                    if (winner != ready)
                    {
                        requestAborted.ThrowIfCancellationRequested();
                        yield return AnswerEvents.Heartbeat();
                    }
                }

                if (!await ready)
                {
                    yield break;
                }

                while (reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            // The producer finalizes the answer, also after a disconnect; the request scope and its DbContext must outlive it.
            await producer;
        }
    }

    private async Task ProduceAsync(AcceptedAnswer answer, ChannelWriter<SseItem<string>> events, CancellationToken aborted)
    {
        var attempt = new Attempt(answer, clock.GetUtcNow(), clock.GetTimestamp(), Activity.Current?.TraceId.ToHexString());
        try
        {
            await RunStagesAsync(attempt, events, aborted);
        }
        catch (Exception) when (aborted.IsCancellationRequested)
        {
            // Client disconnect, whatever the cancelled call threw: nothing more is sent, the answer becomes cancelled and keeps
            // no text (spec 012).
            await FinalizeAsync(attempt, AnswerStatus.Cancelled, null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Spec 012: once question.accepted is sent, every failure leaves the answer failed with an error event; nothing escapes the stream.
            LogUnexpectedFailure(logger, ex, answer.AssistantMessageId);
            await FailAsync(attempt, ChatErrors.InternalCode, events);
        }
        finally
        {
            events.TryComplete();
        }
    }

    private async Task RunStagesAsync(Attempt attempt, ChannelWriter<SseItem<string>> events, CancellationToken aborted)
    {
        var answer = attempt.Answer;
        await events.WriteAsync(
            AnswerEvents.Item(AnswerEvents.QuestionAcceptedType, new AnswerEvents.QuestionAccepted(answer.ConversationId, answer.UserMessageId, answer.AssistantMessageId)),
            aborted);
        var query = await RewriteAsync(attempt, aborted);
        await events.WriteAsync(AnswerEvents.Item(AnswerEvents.RetrievalStartedType, new AnswerEvents.RetrievalStarted(query)), aborted);

        var retrievalStarted = clock.GetTimestamp();
        if (answer.DocumentIds.Length > 0)
        {
            // The scope only decides what is shown: retrieval itself already skips documents that are not active and indexed.
            var availability = await catalog.CheckAsync(answer.DocumentIds, aborted);
            attempt.UnavailableDocumentIds = [.. answer.DocumentIds.Where(id => !availability.Searchable.Contains(id))];
        }

        var filters = new SearchFilters(
            answer.Language, answer.Tags.Length > 0 ? answer.Tags : null, answer.DocumentIds.Length > 0 ? answer.DocumentIds : null);
        var result = await search.SearchAsync(query, filters, retrievalOptions.Value.TopK, aborted);
        attempt.RetrievalMs = Milliseconds(retrievalStarted);
        if (result.IsFailure)
        {
            var code = result.Error.Code == ChatErrors.EmbeddingUnavailableCode ? ChatErrors.EmbeddingUnavailableCode : ChatErrors.InternalCode;
            LogRetrievalFailed(logger, result.Error.Code, answer.AssistantMessageId);
            await FailAsync(attempt, code, events);
            return;
        }

        var found = result.Value;
        attempt.Search = found;
        var sources = found.Chunks
            .Select((c, i) => new AnswerEvents.Source(i + 1, c.PassageId, c.DocumentId, c.DocumentTitle, c.VersionNumber, c.SectionPath, c.PageStart, c.PageEnd))
            .ToList();
        await events.WriteAsync(
            AnswerEvents.Item(AnswerEvents.RetrievalCompletedType, new AnswerEvents.RetrievalCompleted(found.Outcome.ToApi(), sources)),
            aborted);

        if (found.Outcome == EvidenceOutcome.NoEvidence)
        {
            await CompleteAsync(attempt, events);
            return;
        }

        attempt.Filter = new AnswerStreamFilter(found.Chunks.Count, Generation.Sentinel);
        if (await GenerateAsync(attempt, events, aborted))
        {
            await CompleteAsync(attempt, events);
        }
    }

    /// <summary>
    /// Turns a follow-up into a standalone retrieval query (spec 014). Any failure of the rewrite falls back to the original
    /// question; only the caller's cancellation stops the answer.
    /// </summary>
    private async Task<string> RewriteAsync(Attempt attempt, CancellationToken aborted)
    {
        var answer = attempt.Answer;
        attempt.RetrievalQuery = answer.Question;

        // A rewrite is only worth a model call once some earlier answer is complete or partial, however old it is.
        var hasUsableAnswer = await db.Messages.AnyAsync(
            m => m.ConversationId == answer.ConversationId && m.Sequence < answer.UserSequence && m.Role == MessageRole.Assistant
                && (m.Status == AnswerStatus.Complete || m.Status == AnswerStatus.Partial),
            aborted);
        if (!hasUsableAnswer)
        {
            return answer.Question;
        }

        // Only the window is read: an exchange is a question plus its answer, so the last 2N messages cover N exchanges.
        var options = followUpOptions.Value;
        var earlier = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == answer.ConversationId && m.Sequence < answer.UserSequence)
            .OrderByDescending(m => m.Sequence)
            .Take(options.HistoryExchanges * 2)
            .ToListAsync(aborted);
        var history = FollowUpHistory.Build(earlier, options.HistoryExchanges, options.MaxAnswerChars);
        var messages = PromptBuilder.BuildRewrite(history, answer.Question);
        attempt.RewritePromptVersion = PromptBuilder.RewritePromptVersion;

        var started = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(options.RewriteTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, deadline.Token);
        try
        {
            var response = await chat.GetResponseAsync<RewriteResult>(messages, new ChatOptions { MaxOutputTokens = 300 }, useJsonSchemaResponseFormat: null, linked.Token);
            attempt.RewriteMs = Milliseconds(started);
            attempt.RewriteInputTokens = response.Usage?.InputTokenCount;
            attempt.RewriteOutputTokens = response.Usage?.OutputTokenCount;

            var standalone = response.TryGetResult(out var result) ? result.StandaloneQuestion?.Trim() : null;
            if (string.IsNullOrEmpty(standalone) || standalone.Length > AcceptQuestion.MaxQuestionLength)
            {
                attempt.RewriteFallback = RewriteFallback.Invalid;
                return answer.Question;
            }

            attempt.RewriteUsed = true;
            attempt.RetrievalQuery = standalone;
            return standalone;
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            // Our deadline, or a timeout inside the client stack (which also surfaces as a cancellation): neither is the caller leaving.
            var reason = deadline.IsCancellationRequested ? RewriteFallback.Timeout : RewriteFallback.ModelError;
            attempt.RewriteMs = Milliseconds(started);
            attempt.RewriteFallback = reason;
            LogRewriteFallback(logger, answer.AssistantMessageId, reason);
            return answer.Question;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException && !aborted.IsCancellationRequested)
        {
            // The model answered, but not with a usable structure.
            attempt.RewriteMs = Milliseconds(started);
            attempt.RewriteFallback = RewriteFallback.Invalid;
            LogRewriteFallback(logger, answer.AssistantMessageId, RewriteFallback.Invalid);
            return answer.Question;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !aborted.IsCancellationRequested)
        {
            attempt.RewriteMs = Milliseconds(started);
            attempt.RewriteFallback = RewriteFallback.ModelError;
            LogRewriteFallback(logger, answer.AssistantMessageId, RewriteFallback.ModelError);
            return answer.Question;
        }
    }

    /// <returns>False when the model call failed; the answer is then already finalized as failed.</returns>
    private async Task<bool> GenerateAsync(Attempt attempt, ChannelWriter<SseItem<string>> events, CancellationToken aborted)
    {
        var found = attempt.Search!;
        var filter = attempt.Filter!;
        var messages = PromptBuilder.Build(attempt.RetrievalQuery!, found.Chunks, found.Outcome, Generation.Sentinel, attempt.Answer.Question);
        attempt.PromptVersion = PromptBuilder.PromptVersion;
        attempt.ChatModel = chat.GetService<ChatClientMetadata>()?.DefaultModelId ?? "unknown";

        var modelStarted = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(Generation.FirstTokenTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, deadline.Token);
        try
        {
            var options = new ChatOptions { MaxOutputTokens = Generation.MaxOutputTokens };
            await foreach (var update in chat.GetStreamingResponseAsync(messages, options, linked.Token).WithCancellation(linked.Token))
            {
                attempt.ChatModel = update.ModelId ?? attempt.ChatModel;
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    attempt.InputTokens = usage.Details.InputTokenCount ?? attempt.InputTokens;
                    attempt.OutputTokens = usage.Details.OutputTokenCount ?? attempt.OutputTokens;
                }

                var text = update.Text;
                if (text.Length == 0)
                {
                    continue;
                }

                if (attempt.FirstTokenMs is null)
                {
                    attempt.FirstTokenMs = Milliseconds(attempt.StartedTimestamp);
                    var remaining = Generation.TotalTimeout - clock.GetElapsedTime(modelStarted);
                    deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                }

                var released = filter.Push(text);
                if (released.Length > 0)
                {
                    await events.WriteAsync(AnswerEvents.Item(AnswerEvents.AnswerDeltaType, new AnswerEvents.AnswerDelta(released)), aborted);
                }

                if (filter.SentinelHit)
                {
                    break; // the rest of the output is discarded; leaving the loop also ends the model call
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !aborted.IsCancellationRequested)
        {
            LogModelTimeout(logger, attempt.Answer.AssistantMessageId, attempt.FirstTokenMs is null);
            await FailAsync(attempt, ChatErrors.ModelTimeoutCode, events);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !aborted.IsCancellationRequested)
        {
            // Spec 012: any failure of the model call (connection, provider error) is chat.model_unavailable and retryable.
            LogModelFailed(logger, ex, attempt.Answer.AssistantMessageId);
            await FailAsync(attempt, ChatErrors.ModelUnavailableCode, events);
            return false;
        }

        var rest = filter.Flush();
        if (rest.Length > 0)
        {
            await events.WriteAsync(AnswerEvents.Item(AnswerEvents.AnswerDeltaType, new AnswerEvents.AnswerDelta(rest)), aborted);
        }

        return true;
    }

    private async Task CompleteAsync(Attempt attempt, ChannelWriter<SseItem<string>> events)
    {
        var found = attempt.Search!;
        var filter = attempt.Filter;
        var (status, reason) = AnswerStatusRules.Decide(found.Outcome, filter?.SentinelHit ?? false, filter?.CitedMarkers.Count ?? 0);

        var snapshots = status == AnswerStatus.Refused || filter is null
            ? []
            : filter.CitedMarkers.Select(marker => Snapshot(marker, found.Chunks[marker - 1], attempt.Answer.AssistantMessageId)).ToList();
        var text = status == AnswerStatus.Refused ? string.Empty : filter?.Text ?? string.Empty;

        await FinalizeAsync(attempt, status, reason, null, text, snapshots);
        await events.WriteAsync(AnswerEvents.Item(
            AnswerEvents.AnswerCompletedType,
            new AnswerEvents.AnswerCompleted(status.ToApi(), reason?.ToApi(), [.. snapshots.Select(CitationView.From)])));
    }

    private async Task FailAsync(Attempt attempt, string code, ChannelWriter<SseItem<string>> events)
    {
        await FinalizeAsync(attempt, AnswerStatus.Failed, null, code, null);
        await events.WriteAsync(AnswerEvents.Item(
            AnswerEvents.ErrorType,
            new AnswerEvents.StreamError(code, ChatErrors.StreamMessage(code), ChatErrors.IsRetryable(code))));
    }

    private async Task FinalizeAsync(
        Attempt attempt, AnswerStatus status, RefusalReason? reason, string? errorCode, string? text, IReadOnlyList<CitationSnapshot>? snapshots = null)
    {
        using var timeout = new CancellationTokenSource(FinalWriteTimeout);
        var now = clock.GetUtcNow();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
            var message = await db.Messages.Include(m => m.Citations).SingleAsync(m => m.Id == attempt.Answer.AssistantMessageId, timeout.Token);
            message.Status = status;
            message.RefusalReason = reason;
            message.ErrorCode = errorCode;
            message.Text = text ?? string.Empty;
            message.CompletedAt = now;
            message.PendingSince = null;
            if (attempt.RewriteUsed || attempt.RetrievalQuery is not null)
            {
                await db.Messages
                    .Where(m => m.Id == attempt.Answer.UserMessageId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.RetrievalQuery, attempt.RetrievalQuery), timeout.Token);
            }

            message.Citations.Clear();
            message.Citations.AddRange(snapshots ?? []);

            db.RequestTraces.Add(Trace(attempt, status, reason, errorCode));
            await db.SaveChangesAsync(timeout.Token);
            await db.Conversations
                .Where(c => c.Id == attempt.Answer.ConversationId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastActivityAt, now), timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or OperationCanceledException)
        {
            // The answer stays pending; the startup sweep or the stale-answer rule turns it into failed/chat.interrupted.
            LogFinalizeFailed(logger, ex, attempt.Answer.AssistantMessageId, status);
        }
    }

    private RequestTrace Trace(Attempt attempt, AnswerStatus status, RefusalReason? reason, string? errorCode)
    {
        var answer = attempt.Answer;
        var found = attempt.Search;
        var cited = attempt.Filter?.CitedMarkers.ToHashSet() ?? [];
        var passages = found?.Chunks
            .Select((c, i) => new RetrievedPassageTrace(c.PassageId, c.VersionId, c.Score, i + 1, i + 1, status != AnswerStatus.Refused && cited.Contains(i + 1)))
            .ToList() ?? [];

        return new RequestTrace
        {
            Id = Guid.CreateVersion7(attempt.StartedAt),
            ConversationId = answer.ConversationId,
            UserMessageId = answer.UserMessageId,
            AssistantMessageId = answer.AssistantMessageId,
            Attempt = answer.Attempt,
            RetrievalQuery = attempt.RetrievalQuery,
            RewriteUsed = attempt.RewriteUsed,
            RewriteFallbackReason = attempt.RewriteFallback,
            RewriteMs = attempt.RewriteMs,
            RewriteInputTokens = attempt.RewriteInputTokens,
            RewriteOutputTokens = attempt.RewriteOutputTokens,
            RewritePromptVersion = attempt.RewritePromptVersion,
            OwnerSub = answer.OwnerSub,
            QuestionText = answer.Question,
            FilterLanguage = answer.Language,
            FilterTags = answer.Tags,
            FilterDocumentIds = answer.DocumentIds,
            UnavailableDocumentIds = attempt.UnavailableDocumentIds,
            OtelTraceId = attempt.OtelTraceId,
            EvidenceOutcome = found?.Outcome.ToString(),
            MinScore = found?.Thresholds.MinScore,
            ConfidentScore = found?.Thresholds.ConfidentScore,
            DuplicateOverlap = found?.Thresholds.DuplicateOverlap,
            EmbeddingModel = found?.EmbeddingModel,
            RetrievedPassages = JsonSerializer.Serialize(passages, TraceJson),
            ChatModel = attempt.ChatModel,
            PromptVersion = attempt.PromptVersion,
            InputTokens = attempt.InputTokens,
            OutputTokens = attempt.OutputTokens,
            SentinelHit = attempt.Filter?.SentinelHit ?? false,
            StrippedMarkerCount = attempt.Filter?.StrippedMarkerCount ?? 0,
            FinalStatus = status,
            RefusalReason = reason,
            ErrorCode = errorCode,
            StartedAt = attempt.StartedAt,
            RetrievalMs = attempt.RetrievalMs,
            FirstTokenMs = attempt.FirstTokenMs,
            TotalMs = Milliseconds(attempt.StartedTimestamp),
        };
    }

    private static CitationSnapshot Snapshot(int marker, RetrievedChunk chunk, Guid messageId) => new()
    {
        Id = Guid.CreateVersion7(),
        MessageId = messageId,
        Marker = marker,
        PassageId = chunk.PassageId,
        DocumentId = chunk.DocumentId,
        VersionId = chunk.VersionId,
        VersionNumber = chunk.VersionNumber,
        DocumentTitle = chunk.DocumentTitle,
        SectionPath = [.. chunk.SectionPath],
        PageStart = chunk.PageStart,
        PageEnd = chunk.PageEnd,
        Score = chunk.Score,
        Text = chunk.Text,
    };

    private int Milliseconds(long since) => (int)Math.Min(clock.GetElapsedTime(since).TotalMilliseconds, int.MaxValue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The follow-up rewrite fell back to the original question for answer {AssistantMessageId}: {Reason}")]
    private static partial void LogRewriteFallback(ILogger logger, Guid assistantMessageId, RewriteFallback reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrieval failed with {Code} for answer {AssistantMessageId}")]
    private static partial void LogRetrievalFailed(ILogger logger, string code, Guid assistantMessageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The chat model timed out for answer {AssistantMessageId} (before the first token: {BeforeFirstToken})")]
    private static partial void LogModelTimeout(ILogger logger, Guid assistantMessageId, bool beforeFirstToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The chat model failed for answer {AssistantMessageId}")]
    private static partial void LogModelFailed(ILogger logger, Exception exception, Guid assistantMessageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Answer {AssistantMessageId} failed unexpectedly")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception exception, Guid assistantMessageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not persist answer {AssistantMessageId} as {Status}; it stays pending until swept")]
    private static partial void LogFinalizeFailed(ILogger logger, Exception exception, Guid assistantMessageId, AnswerStatus status);

    /// <summary>What one attempt has learned so far; becomes the request trace.</summary>
    private sealed class Attempt(AcceptedAnswer answer, DateTimeOffset startedAt, long startedTimestamp, string? otelTraceId)
    {
        public AcceptedAnswer Answer { get; } = answer;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public long StartedTimestamp { get; } = startedTimestamp;

        public string? OtelTraceId { get; } = otelTraceId;

        public SearchResult? Search { get; set; }

        public Guid[] UnavailableDocumentIds { get; set; } = [];

        public AnswerStreamFilter? Filter { get; set; }

        public int? RetrievalMs { get; set; }

        public int? FirstTokenMs { get; set; }

        public string? ChatModel { get; set; }

        /// <summary>What retrieval searched for; null until the rewrite step has run.</summary>
        public string? RetrievalQuery { get; set; }

        public bool RewriteUsed { get; set; }

        public RewriteFallback? RewriteFallback { get; set; }

        public int? RewriteMs { get; set; }

        public long? RewriteInputTokens { get; set; }

        public long? RewriteOutputTokens { get; set; }

        public string? RewritePromptVersion { get; set; }

        public string? PromptVersion { get; set; }

        public long? InputTokens { get; set; }

        public long? OutputTokens { get; set; }
    }
}
