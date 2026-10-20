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

/// <summary>
/// The one retrieval pipeline behind both <see cref="IDocumentSearch"/> and the diagnostic endpoint (spec 011): query embedding,
/// vector pool, in hybrid mode a keyword pool fused with it (spec 019), optional reranking, then floor, duplicates and cut.
/// </summary>
internal static partial class SearchPassages
{
    public const int MaxQueryLength = 1000;
    public const int MaxTags = 20;

    /// <param name="TopK">Raw text, so a malformed value surfaces as a field error; empty means <c>DefaultTopK</c>.</param>
    /// <param name="IncludeDiagnostics">Also counts documents skipped for a model mismatch; only the endpoint needs it.</param>
    /// <param name="Candidates">Raw <c>candidates</c> text (<c>true</c>/<c>false</c>); true also returns the scored pool (spec 018, threshold sweep).</param>
    /// <param name="Mode">Raw <c>vector</c>/<c>hybrid</c>, overriding the configuration for one call (spec 019); empty uses it.</param>
    /// <param name="Rerank">Raw <c>true</c>/<c>false</c>, overriding the configuration for one call (spec 019); empty uses it.</param>
    /// <param name="QueryLanguage">Two letters: the keyword configuration of the query (spec 019); null is language-neutral.</param>
    public sealed record Query(
        string? Q,
        string? Language,
        string[] Tags,
        string? TopK,
        bool IncludeDiagnostics,
        IReadOnlyList<Guid>? DocumentIds = null,
        string? ViewerSub = null,
        string? Candidates = null,
        string? Mode = null,
        string? Rerank = null,
        string? QueryLanguage = null)
        : IRequest<Result<SearchRun>>;

    /// <param name="SkippedForModelMismatch">Null unless diagnostics were requested.</param>
    /// <param name="Pool">The scored pool in the order the gate saw it, when <c>candidates</c> was asked for.</param>
    public sealed record SearchRun(
        string Query,
        int TopK,
        EvidenceOutcome Outcome,
        IReadOnlyList<RetrievedChunk> Kept,
        IReadOnlyList<DiscardedPassage> Discarded,
        EvidenceThresholds Thresholds,
        string EmbeddingModel,
        int? SkippedForModelMismatch,
        double? BestScore = null,
        IReadOnlyList<RetrievedChunk>? Pool = null,
        RetrievalMode Mode = RetrievalMode.Vector,
        RerankStatus Rerank = RerankStatus.Off,
        int? RerankMs = null,
        string? RerankModel = null,
        string? QueryLanguage = null);

    public static bool WantsCandidates(string? value) => bool.TryParse(value, out var wanted) && wanted;

    /// <summary><c>vector</c> or <c>hybrid</c>, case-insensitive; null for anything else.</summary>
    public static RetrievalMode? ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vector" => RetrievalMode.Vector,
        "hybrid" => RetrievalMode.Hybrid,
        _ => null,
    };

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

            RuleFor(x => x.ViewerSub)
                .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("A viewer is required.");

            RuleFor(x => x.Language)
                .Must(IsLanguageOrEmpty)
                .WithMessage("'language' must be two letters.");

            RuleFor(x => x.Tags)
                .Must(v => v.Length <= MaxTags)
                .WithMessage($"At most {MaxTags} tags can be combined.");

            RuleFor(x => x.Candidates)
                .Must(v => string.IsNullOrWhiteSpace(v) || bool.TryParse(v, out _))
                .WithMessage("'candidates' must be true or false.");

            RuleFor(x => x.Mode)
                .Must(v => string.IsNullOrWhiteSpace(v) || ParseMode(v) is not null)
                .WithMessage("'mode' must be vector or hybrid.");

            RuleFor(x => x.Rerank)
                .Must(v => string.IsNullOrWhiteSpace(v) || bool.TryParse(v, out _))
                .WithMessage("'rerank' must be true or false.");

            RuleFor(x => x.QueryLanguage)
                .Must(IsLanguageOrEmpty)
                .WithMessage("'queryLanguage' must be two letters.");
        }

        private static bool IsLanguageOrEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) || (value.Trim().Length == 2 && value.Trim().All(char.IsAsciiLetter));
    }

    internal sealed partial class Handler(
        QueryEmbedder embedder,
        PassageCandidates candidates,
        PassageReranking reranking,
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
                DocumentMetadata.NormalizeTags(query.Tags.Where(t => !string.IsNullOrWhiteSpace(t))),
                query.DocumentIds)
            {
                ViewerSub = query.ViewerSub!,
            };
            var mode = ParseMode(query.Mode) ?? (settings.Hybrid.Enabled ? RetrievalMode.Hybrid : RetrievalMode.Vector);
            var rerank = bool.TryParse(query.Rerank, out var asked) ? asked : settings.Reranking.Enabled;
            var queryLanguage = string.IsNullOrWhiteSpace(query.QueryLanguage) ? null : query.QueryLanguage.Trim().ToLowerInvariant();
            var poolSize = settings.PoolSize(topK);

            using var activity = DocumentsTelemetry.Source.StartActivity("documents.search");
            activity?.SetTag("documents.search.top_k", topK);
            activity?.SetTag("documents.search.filter.language", filter.Language is not null);
            activity?.SetTag("documents.search.filter.tags", filter.Tags.Count > 0);
            activity?.SetTag("documents.search.mode", ModeName(mode));
            LogQuery(logger, text);
            var started = clock.GetTimestamp();
            var model = embedder.ModelId;

            // Spec 019, latency: the keyword pool needs no vector, so it is fetched while the query is embedded.
            var embedding = embedder.EmbedAsync(text, cancellationToken);
            var keywords = mode == RetrievalMode.Hybrid
                ? candidates.KeywordAsync(text, queryLanguage, model, filter, poolSize, cancellationToken)
                : Task.FromResult<IReadOnlyList<KeywordHit>>([]);
            await Task.WhenAll(embedding, keywords);
            var embedded = await embedding;
            if (embedded.IsFailure)
            {
                activity?.SetStatus(ActivityStatusCode.Error, embedded.Error.Code);
                metrics.SearchFailed(embedded.Error.Code, clock.GetElapsedTime(started));
                return Result.Failure<SearchRun>(embedded.Error);
            }

            var vector = embedded.Value.Vector;
            var vectorPool = await candidates.FetchAsync(vector, model, filter, poolSize, cancellationToken);
            var keywordHits = await keywords;
            List<RetrievedChunk> ordered;
            if (mode == RetrievalMode.Hybrid)
            {
                var inVectorPool = vectorPool.Select(p => p.PassageId).ToHashSet();
                Guid[] missing = [.. keywordHits.Select(h => h.PassageId).Where(id => !inVectorPool.Contains(id))];
                var keywordOnly = missing.Length == 0 ? [] : await candidates.FetchByIdsAsync(vector, model, filter, missing, cancellationToken);
                ordered = HybridRanking.Hybrid(vectorPool, keywordHits, keywordOnly, settings.Hybrid.RrfK);
                metrics.KeywordHits(keywordHits.Count);
                activity?.SetTag("documents.search.keyword_hits", keywordHits.Count);
            }
            else
            {
                ordered = HybridRanking.Vector(vectorPool);
            }

            var thresholds = new EvidenceThresholds(settings.MinScore, settings.ConfidentScore, settings.DuplicateOverlap);
            var rerankStatus = RerankStatus.Off;
            int? rerankMs = null;
            string? rerankModel = null;
            if (rerank)
            {
                var head = ordered.Take(settings.Reranking.Candidates).ToList();
                var outcome = await reranking.RerankAsync(text, head, settings.Reranking.Timeout, cancellationToken);
                rerankMs = outcome.Ms;
                rerankModel = outcome.Model;
                if (outcome.Scores is { } scores)
                {
                    // Spec 019, evidence gate: once reranked, the floor and the confident score apply to the rerank score.
                    ordered = HybridRanking.Rerank(head, scores);
                    thresholds = new EvidenceThresholds(settings.Reranking.MinScore, settings.Reranking.ConfidentScore, settings.DuplicateOverlap);
                    rerankStatus = RerankStatus.Applied;
                }
                else
                {
                    rerankStatus = RerankStatus.Skipped;
                }
            }

            // Spec 019: while cosine is the gate, a keyword match that covers enough of the question passes the cosine floor, which
            // would otherwise drop the exact-term matches dense vectors miss. A rerank score already judged every candidate.
            IReadOnlySet<Guid>? pastFloor = rerankStatus == RerankStatus.Applied
                ? null
                : keywordHits.Where(h => h.Coverage >= settings.Hybrid.MinKeywordCoverage).Select(h => h.PassageId).ToHashSet();
            var decision = EvidenceRules.ApplyOrdered(ordered, topK, thresholds, pastFloor);
            int? skipped = query.IncludeDiagnostics
                ? await candidates.CountModelMismatchesAsync(model, filter, cancellationToken)
                : null;

            activity?.SetTag("documents.search.rerank", RerankName(rerankStatus));
            activity?.SetTag("documents.search.embedding.cache_hit", embedded.Value.CacheHit);
            activity?.SetTag("documents.search.candidates", ordered.Count);
            activity?.SetTag("documents.search.returned", decision.Kept.Count);
            var outcomeName = OutcomeName(decision.Outcome);
            activity?.SetTag("documents.search.outcome", outcomeName);
            if (decision.Kept.Count > 0)
            {
                activity?.SetTag("documents.search.best_score", decision.Kept.Max(k => k.Score));
            }

            metrics.SearchCompleted(outcomeName, clock.GetElapsedTime(started));
            LogCompleted(logger, outcomeName, ordered.Count, decision.Kept.Count);

            double? best = ordered.Count > 0 ? ordered.Max(c => c.Score) : null;
            return new SearchRun(
                text, topK, decision.Outcome, decision.Kept, decision.Discarded, thresholds, model, skipped, best,
                WantsCandidates(query.Candidates) ? ordered : null,
                mode, rerankStatus, rerankMs, rerankModel, queryLanguage);
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

    public static string ModeName(RetrievalMode mode) => mode == RetrievalMode.Hybrid ? "hybrid" : "vector";

    public static string RerankName(RerankStatus status) => status switch
    {
        RerankStatus.Applied => "applied",
        RerankStatus.Skipped => "skipped",
        _ => "off",
    };

    public static string AdmittedName(AdmittedBy admitted) => admitted switch
    {
        AdmittedBy.Keyword => "keyword",
        AdmittedBy.Both => "both",
        _ => "vector",
    };
}
