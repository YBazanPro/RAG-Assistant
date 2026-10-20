using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Documents.Retrieval;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using RagAssistant.SharedKernel.Contracts.Documents;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.SearchPassages;

/// <summary>Diagnostic search for maintainers: ranked passages, scores and every decision taken (spec 011, step 2.4).</summary>
public sealed class SearchPassagesEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/search", async (
                string? q,
                string? language,
                string[]? tags,
                string? topK,
                string? candidates,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(
                    new SearchPassages.Query(q, language, tags ?? [], topK, IncludeDiagnostics: true, ViewerSub: user.GetSubject(), Candidates: candidates),
                    cancellationToken))
                    .ToHttpResult(run => TypedResults.Ok(SearchDiagnostics.From(run))))
            .RequireAuthorization(PolicyNames.Maintainer)
            .WithName("SearchPassages")
            .Produces<SearchDiagnostics>();
    }
}

public sealed record SearchDiagnostics(
    string Query,
    string Outcome,
    int TopK,
    string EmbeddingModel,
    SearchThresholds Thresholds,
    int SkippedForModelMismatch,
    IReadOnlyList<RankedPassage> Passages,
    IReadOnlyList<DiscardedCandidate> Discarded,
    IReadOnlyList<SearchCandidate>? Candidates = null)
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
            p.PageStart, p.PageEnd, p.Text, Round(p.Score)))],
        [.. run.Discarded.Select(d => new DiscardedCandidate(
            d.Passage.PassageId, d.Passage.DocumentId, d.Passage.DocumentTitle, d.Passage.VersionNumber, d.Passage.Position,
            Round(d.Passage.Score),
            d.Reason == DiscardReason.Duplicate ? "duplicate" : "below_min_score",
            d.DuplicateOf,
            d.Overlap is { } overlap ? Round(overlap) : null))],
        run.Pool is null ? null : RankedPool(run));

    /// <summary>The whole scored pool in rank order, each with what dropped it (spec 018): the harness sweeps thresholds over it.</summary>
    private static List<SearchCandidate> RankedPool(SearchPassages.SearchRun run)
    {
        var kept = run.Kept.Select(p => p.PassageId).ToHashSet();
        var discarded = run.Discarded.ToDictionary(d => d.Passage.PassageId, d => d.Reason);
        return [.. run.Pool!.Select(p => new SearchCandidate(
            p.PassageId, p.DocumentId, p.Language, Round(p.Score),
            kept.Contains(p.PassageId) ? null
                : discarded.TryGetValue(p.PassageId, out var reason) ? (reason == DiscardReason.Duplicate ? "duplicate" : "minScore")
                : "topK"))];
    }

    private static double Round(double value) => Math.Round(value, ScoreDecimals, MidpointRounding.AwayFromZero);
}

public sealed record SearchThresholds(double MinScore, double ConfidentScore, double DuplicateOverlap);

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
    double Score);

/// <param name="Dropped">Null when kept; otherwise <c>minScore</c>, <c>duplicate</c> or <c>topK</c>.</param>
public sealed record SearchCandidate(Guid PassageId, Guid DocumentId, string Language, double Score, string? Dropped);

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
    double? Overlap);
