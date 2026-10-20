using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Documents.Retrieval;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using RagAssistant.SharedKernel.Common.Text;
using RagAssistant.SharedKernel.Contracts.Documents;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.SearchPassages;

/// <summary>Diagnostic search for maintainers: ranked passages, scores and every decision taken (spec 011, step 2.4; spec 019 stages).</summary>
public sealed class SearchPassagesEndpoint : IEndpointGroup
{
    /// <summary>Questions are short; the chat's question minimum applies (spec 018, clarification 1).</summary>
    private const int QueryMinWords = 3;

    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/search", async (
                string? q,
                string? language,
                string[]? tags,
                string? topK,
                string? candidates,
                string? mode,
                string? rerank,
                string? queryLanguage,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(
                    new SearchPassages.Query(
                        q, language, tags ?? [], topK, IncludeDiagnostics: true, ViewerSub: user.GetSubject(), Candidates: candidates,
                        Mode: mode, Rerank: rerank, QueryLanguage: QueryLanguageOf(queryLanguage, q)),
                    cancellationToken))
                    .ToHttpResult(run => TypedResults.Ok(SearchDiagnostics.From(run))))
            .RequireAuthorization(PolicyNames.Maintainer)
            .WithName("SearchPassages")
            .Produces<SearchDiagnostics>();
    }

    /// <summary>The given language, else the one the detector is sure of (spec 019: detected when absent), else none.</summary>
    private static string? QueryLanguageOf(string? given, string? query) =>
        !string.IsNullOrWhiteSpace(given) ? given
        : LanguageDetector.Detect(query, QueryMinWords) is { IsConfident: true, Language: { } detected } ? detected
        : null;
}

/// <param name="Mode"><c>vector</c> or <c>hybrid</c> (spec 019).</param>
/// <param name="Rerank"><c>off</c>, <c>applied</c> or <c>skipped</c> (spec 019).</param>
/// <param name="QueryLanguage">The keyword configuration's language; null when none was given or detected.</param>
public sealed record SearchDiagnostics(
    string Query,
    string Outcome,
    int TopK,
    string EmbeddingModel,
    SearchThresholds Thresholds,
    int SkippedForModelMismatch,
    IReadOnlyList<RankedPassage> Passages,
    IReadOnlyList<DiscardedCandidate> Discarded,
    IReadOnlyList<SearchCandidate>? Candidates = null,
    string Mode = "vector",
    string Rerank = "off",
    int? RerankMs = null,
    string? RerankModel = null,
    string? QueryLanguage = null)
{
    private const int ScoreDecimals = 4;

    internal static SearchDiagnostics From(SearchPassages.SearchRun run) => new(
        run.Query,
        SearchPassages.OutcomeName(run.Outcome),
        run.TopK,
        run.EmbeddingModel,
        new SearchThresholds(run.Thresholds.MinScore, run.Thresholds.ConfidentScore, run.Thresholds.DuplicateOverlap),
        run.SkippedForModelMismatch ?? 0,
        [.. run.Kept.Select((p, i) => new RankedPassage(
            i + 1, p.PassageId, p.DocumentId, p.DocumentTitle, p.Language, p.VersionNumber, p.SectionPath, p.Position,
            p.PageStart, p.PageEnd, p.Text, Round(p.Score), StagesOf(p)))],
        [.. run.Discarded.Select(d => new DiscardedCandidate(
            d.Passage.PassageId, d.Passage.DocumentId, d.Passage.DocumentTitle, d.Passage.VersionNumber, d.Passage.Position,
            Round(d.Passage.Score),
            d.Reason == DiscardReason.Duplicate ? "duplicate" : "below_min_score",
            d.DuplicateOf,
            d.Overlap is { } overlap ? Round(overlap) : null,
            StagesOf(d.Passage)))],
        run.Pool is null ? null : RankedPool(run),
        SearchPassages.ModeName(run.Mode),
        SearchPassages.RerankName(run.Rerank),
        run.RerankMs,
        run.RerankModel,
        run.QueryLanguage);

    /// <summary>The whole scored pool in the order the gate saw it, each with what dropped it (spec 018): the harness sweeps thresholds over it.</summary>
    private static List<SearchCandidate> RankedPool(SearchPassages.SearchRun run)
    {
        var kept = run.Kept.Select(p => p.PassageId).ToHashSet();
        var discarded = run.Discarded.ToDictionary(d => d.Passage.PassageId, d => d.Reason);
        return [.. run.Pool!.Select(p => new SearchCandidate(
            p.PassageId, p.DocumentId, p.Language, Round(p.Score),
            kept.Contains(p.PassageId) ? null
                : discarded.TryGetValue(p.PassageId, out var reason) ? (reason == DiscardReason.Duplicate ? "duplicate" : "minScore")
                : "topK",
            StagesOf(p)))];
    }

    internal static SearchStages? StagesOf(RetrievedChunk chunk) => chunk.Stages is not { } s ? null : new SearchStages(
        s.VectorRank, Round(s.Cosine), s.KeywordRank, s.KeywordScore is { } k ? Round(k) : null, s.FusedScore is { } f ? Round(f, 6) : null,
        s.RerankScore is { } r ? Round(r) : null, SearchPassages.AdmittedName(s.AdmittedBy), s.KeywordCoverage is { } c ? Round(c) : null);

    private static double Round(double value, int decimals = ScoreDecimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}

public sealed record SearchThresholds(double MinScore, double ConfidentScore, double DuplicateOverlap);

/// <summary>What each retrieval stage said about a passage (spec 019); a stage that did not see it leaves its fields null.</summary>
/// <param name="AdmittedBy"><c>vector</c>, <c>keyword</c> or <c>both</c>.</param>
/// <param name="KeywordCoverage">The share of the question's terms in the passage; at least <c>Hybrid:MinKeywordCoverage</c> passes the floor.</param>
public sealed record SearchStages(
    int? VectorRank,
    double Cosine,
    int? KeywordRank,
    double? KeywordScore,
    double? FusedScore,
    double? RerankScore,
    string AdmittedBy,
    double? KeywordCoverage = null);

public sealed record RankedPassage(
    int Rank,
    Guid PassageId,
    Guid DocumentId,
    string DocumentTitle,
    string Language,
    int VersionNumber,
    IReadOnlyList<string> SectionPath,
    int Position,
    int? PageStart,
    int? PageEnd,
    string Text,
    double Score,
    SearchStages? Stages = null);

/// <param name="Dropped">Null when kept; otherwise <c>minScore</c>, <c>duplicate</c> or <c>topK</c>.</param>
public sealed record SearchCandidate(Guid PassageId, Guid DocumentId, string Language, double Score, string? Dropped, SearchStages? Stages = null);

/// <summary>A candidate dropped by the floor or as a duplicate; no text, the passage endpoints have it.</summary>
public sealed record DiscardedCandidate(
    Guid PassageId,
    Guid DocumentId,
    string DocumentTitle,
    int VersionNumber,
    int Position,
    double Score,
    string Reason,
    Guid? DuplicateOf,
    double? Overlap,
    SearchStages? Stages = null);
