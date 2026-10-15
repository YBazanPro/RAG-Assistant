using System.Diagnostics;
using System.Globalization;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Retrieval;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Features.SearchPassages;

/// <summary>The one retrieval pipeline behind both <see cref="IDocumentSearch"/> and the diagnostic endpoint (spec 011).</summary>
internal static partial class SearchPassages
{
    public const int MaxQueryLength = 1000;
    public const int MaxTags = 20;

    /// <param name="TopK">Raw text, so a malformed value surfaces as a field error; empty means <c>DefaultTopK</c>.</param>
    /// <param name="IncludeDiagnostics">Also counts documents skipped for a model mismatch; only the endpoint needs it.</param>
    public sealed record Query(string? Q, string? Language, string[] Tags, string? TopK, bool IncludeDiagnostics)
        : IRequest<Result<SearchRun>>;

    /// <param name="SkippedForModelMismatch">Null unless diagnostics were requested.</param>
    public sealed record SearchRun(
        string Query,
        int TopK,
        EvidenceOutcome Outcome,
        IReadOnlyList<RetrievedChunk> Kept,
        IReadOnlyList<DiscardedPassage> Discarded,
        EvidenceThresholds Thresholds,
        string EmbeddingModel,
        int? SkippedForModelMismatch);

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator(IOptions<RetrievalOptions> options)
        {
            var maxTopK = options.Value.MaxTopK;

            RuleFor(x => x.Q)
                .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("'q' is required.")
                .Must(v => v is null || v.Trim().Length <= MaxQueryLength)
                .WithMessage($"'q' must be at most {MaxQueryLength} characters.");

            RuleFor(x => x.TopK)
                .Must(v => string.IsNullOrWhiteSpace(v)
                    || (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1 && k <= maxTopK))
                .WithMessage($"'topK' must be a whole number from 1 to {maxTopK}.");

            RuleFor(x => x.Language)
                .Must(v => string.IsNullOrWhiteSpace(v) || (v.Trim().Length == 2 && v.Trim().All(char.IsAsciiLetter)))
                .WithMessage("'language' must be two letters.");

            RuleFor(x => x.Tags)
                .Must(v => v.Length <= MaxTags)
                .WithMessage($"At most {MaxTags} tags can be combined.");
        }
    }

    internal sealed partial class Handler(
        QueryEmbedder embedder,
        PassageCandidates candidates,
        IOptionsSnapshot<RetrievalOptions> options,
        DocumentsMetrics metrics,
        TimeProvider clock,
        ILogger<Handler> logger) : IRequestHandler<Query, Result<SearchRun>>
    {
        public async ValueTask<Result<SearchRun>> Handle(Query query, CancellationToken cancellationToken)
        {
            var settings = options.Value;
            var text = query.Q!.Trim();
            var topK = string.IsNullOrWhiteSpace(query.TopK) ? settings.DefaultTopK : int.Parse(query.TopK, CultureInfo.InvariantCulture);
            var filter = new CandidateFilter(
                string.IsNullOrWhiteSpace(query.Language) ? null : query.Language.Trim().ToLowerInvariant(),
                DocumentMetadata.NormalizeTags(query.Tags.Where(t => !string.IsNullOrWhiteSpace(t))));
            var thresholds = new EvidenceThresholds(settings.MinScore, settings.ConfidentScore, settings.DuplicateOverlap);

            using var activity = DocumentsTelemetry.Source.StartActivity("documents.search");
            activity?.SetTag("documents.search.top_k", topK);
            activity?.SetTag("documents.search.filter.language", filter.Language is not null);
            activity?.SetTag("documents.search.filter.tags", filter.Tags.Count > 0);
            LogQuery(logger, text);
            var started = clock.GetTimestamp();

            var embedded = await embedder.EmbedAsync(text, cancellationToken);
            if (embedded.IsFailure)
            {
                activity?.SetStatus(ActivityStatusCode.Error, embedded.Error.Code);
                metrics.SearchFailed(embedded.Error.Code, clock.GetElapsedTime(started));
                return Result.Failure<SearchRun>(embedded.Error);
            }

            var model = embedder.ModelId;
            var pool = await candidates.FetchAsync(embedded.Value.Vector, model, filter, settings.PoolSize(topK), cancellationToken);
            var decision = EvidenceRules.Apply(pool, topK, thresholds);
            int? skipped = query.IncludeDiagnostics
                ? await candidates.CountModelMismatchesAsync(model, filter, cancellationToken)
                : null;

            activity?.SetTag("documents.search.embedding.cache_hit", embedded.Value.CacheHit);
            activity?.SetTag("documents.search.candidates", pool.Count);
            activity?.SetTag("documents.search.returned", decision.Kept.Count);
            var outcome = OutcomeName(decision.Outcome);
            activity?.SetTag("documents.search.outcome", outcome);
            if (decision.Kept.Count > 0)
            {
                activity?.SetTag("documents.search.best_score", decision.Kept[0].Score);
            }

            metrics.SearchCompleted(outcome, clock.GetElapsedTime(started));
            LogCompleted(logger, outcome, pool.Count, decision.Kept.Count);

            return new SearchRun(text, topK, decision.Outcome, decision.Kept, decision.Discarded, thresholds, model, skipped);
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Searching passages for \"{Query}\".")]
        private static partial void LogQuery(ILogger logger, string query);

        [LoggerMessage(Level = LogLevel.Information, Message = "Passage search: {Outcome}, {Candidates} candidates, {Returned} returned.")]
        private static partial void LogCompleted(ILogger logger, string outcome, int candidates, int returned);
    }

    /// <summary>camelCase, as the endpoint and the telemetry show it: noEvidence, lowConfidence, sufficient.</summary>
    public static string OutcomeName(EvidenceOutcome outcome) => outcome switch
    {
        EvidenceOutcome.NoEvidence => "noEvidence",
        EvidenceOutcome.LowConfidence => "lowConfidence",
        _ => "sufficient",
    };
}
