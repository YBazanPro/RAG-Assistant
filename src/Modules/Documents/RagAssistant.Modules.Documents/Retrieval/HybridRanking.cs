using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <summary>
/// Orders the candidate pool for each mode and records every stage's numbers on the passages (spec 019). Pure: pools in, ordered
/// pool out. <see cref="RetrievedChunk.Score"/> stays the cosine until reranking replaces it with the rerank score.
/// </summary>
internal static class HybridRanking
{
    /// <summary>Vector mode: cosine order, as before spec 019.</summary>
    public static List<RetrievedChunk> Vector(IEnumerable<RetrievedChunk> vectorPool) =>
        [.. EvidenceRules.Rank(vectorPool).Select((c, i) => c with { Stages = new PassageStages(i + 1, c.Score, null, null, null, null, AdmittedBy.Vector) })];

    /// <summary>Reciprocal rank fusion: Σ 1 / (k + rank) over the pools that ranked the passage.</summary>
    public static double Fuse(int k, int? vectorRank, int? keywordRank) =>
        (vectorRank is { } v ? 1.0 / (k + v) : 0) + (keywordRank is { } w ? 1.0 / (k + w) : 0);

    /// <summary>
    /// Hybrid mode: the vector pool and the keyword pool merged by <see cref="Fuse"/>, best first.
    /// </summary>
    /// <param name="keywordHits">Keyword matches, best first; every one is in <paramref name="vectorPool"/> or <paramref name="keywordOnly"/>.</param>
    /// <param name="keywordOnly">The keyword matches the vector pool missed, with their cosine.</param>
    public static List<RetrievedChunk> Hybrid(
        IEnumerable<RetrievedChunk> vectorPool, IReadOnlyList<KeywordHit> keywordHits, IEnumerable<RetrievedChunk> keywordOnly, int k)
    {
        var ranked = EvidenceRules.Rank(vectorPool);
        var vectorRanks = ranked.Select((c, i) => (c.PassageId, Rank: i + 1)).ToDictionary(x => x.PassageId, x => x.Rank);
        var keyword = keywordHits.Select((h, i) => (h, Rank: i + 1)).ToDictionary(x => x.h.PassageId, x => (x.Rank, x.h.Score));

        var merged = ranked.Concat(keywordOnly.Where(c => !vectorRanks.ContainsKey(c.PassageId)));
        return
        [
            .. merged
                .Select(c =>
                {
                    int? vectorRank = vectorRanks.TryGetValue(c.PassageId, out var v) ? v : null;
                    (int Rank, double Score)? match = keyword.TryGetValue(c.PassageId, out var w) ? w : null;
                    var admitted = vectorRank is null ? AdmittedBy.Keyword : match is null ? AdmittedBy.Vector : AdmittedBy.Both;
                    var fused = Fuse(k, vectorRank, match?.Rank);
                    return c with { Stages = new PassageStages(vectorRank, c.Score, match?.Rank, match?.Score, fused, null, admitted) };
                })
                .OrderByDescending(c => c.Stages!.FusedScore)
                .ThenByDescending(c => c.Score)
                .ThenBy(c => c.DocumentId)
                .ThenBy(c => c.Position),
        ];
    }

    /// <summary>
    /// The reranked head of the pool, best first, with the rerank score as the gating score; candidates past the head were not
    /// scored and leave the pool (spec 019, flow).
    /// </summary>
    /// <param name="scores">One score per passage of <paramref name="head"/>, in its order.</param>
    public static List<RetrievedChunk> Rerank(IReadOnlyList<RetrievedChunk> head, IReadOnlyList<double> scores) =>
    [
        .. head
            .Select((c, i) => c with { Score = scores[i], Stages = (c.Stages ?? Unstaged(c)) with { RerankScore = scores[i] } })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.DocumentId)
            .ThenBy(c => c.Position),
    ];

    private static PassageStages Unstaged(RetrievedChunk chunk) => new(null, chunk.Score, null, null, null, null, AdmittedBy.Vector);
}
