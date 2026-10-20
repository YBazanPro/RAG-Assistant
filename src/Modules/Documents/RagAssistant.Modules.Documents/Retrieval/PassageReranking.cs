using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.SharedKernel.Contracts.Documents;
using RagAssistant.SharedKernel.Contracts.Retrieval;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <param name="Scores">One per candidate, in their order; null when reranking was skipped.</param>
internal sealed record RerankOutcome(IReadOnlyList<double>? Scores, int Ms, string? Model);

/// <summary>
/// The rerank step of retrieval (spec 019): one call for all candidates, bounded by its own deadline. Any failure of the reranker
/// (not configured, unreachable, a timeout, a wrong number of scores) means "skipped", never a failed search.
/// </summary>
internal sealed partial class PassageReranking(IPassageReranker reranker, DocumentsMetrics metrics, TimeProvider clock, ILogger<PassageReranking> logger)
{
    public string? ModelId => reranker.ModelId;

    public async Task<RerankOutcome> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var activity = DocumentsTelemetry.Source.StartActivity("documents.rerank");
        activity?.SetTag("documents.rerank.candidates", candidates.Count);
        var started = clock.GetTimestamp();
        if (candidates.Count == 0)
        {
            activity?.SetTag("documents.rerank.outcome", "applied");
            return new RerankOutcome([], 0, reranker.ModelId);
        }

        // The same context as the embedded text (spec 021): the title and the heading path say what the passage is about.
        string[] texts = [.. candidates.Select(c => EmbeddingInput.Build(c.DocumentTitle, c.SectionPath, c.Text))];
        string? failure = null;
        IReadOnlyList<double>? scores = null;
        try
        {
            using var deadline = new CancellationTokenSource(timeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            scores = await reranker.RerankAsync(query, texts, linked.Token);
            if (scores.Count != texts.Length)
            {
                failure = "wrong_count";
                scores = null;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Spec 019, fallback: whatever the reranker throws (not configured, connection refused, 5xx, our deadline), the search
            // goes on gated by cosine. Only the caller's own cancellation stops it.
            failure = ex is OperationCanceledException ? "timeout" : "unavailable";
            LogSkipped(logger, failure, ex.GetType().Name);
        }

        var elapsed = clock.GetElapsedTime(started);
        var ms = (int)Math.Min(elapsed.TotalMilliseconds, int.MaxValue);
        activity?.SetTag("documents.rerank.ms", ms);
        if (scores is null)
        {
            activity?.SetTag("documents.rerank.outcome", "skipped");
            activity?.SetTag("documents.rerank.reason", failure);
            metrics.RerankSkipped(failure ?? "unavailable");
            if (failure == "wrong_count")
            {
                LogSkipped(logger, failure, "count");
            }

            return new RerankOutcome(null, ms, reranker.ModelId);
        }

        activity?.SetTag("documents.rerank.outcome", "applied");
        metrics.RerankCompleted(elapsed);
        return new RerankOutcome(scores, ms, reranker.ModelId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranking skipped ({Reason}, {Detail}); the search is gated by cosine.")]
    private static partial void LogSkipped(ILogger logger, string reason, string detail);
}

/// <summary>Registered when the host provides no reranker: every call fails, so reranking is always skipped (spec 019).</summary>
internal sealed class UnconfiguredReranker : IPassageReranker
{
    public string? ModelId => null;

    public Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No reranker is configured.");
}
