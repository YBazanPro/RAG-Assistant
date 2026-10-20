using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Retrieval;

internal enum DiscardReason
{
    BelowMinScore,
    Duplicate,
}

internal sealed record DiscardedPassage(RetrievedChunk Passage, DiscardReason Reason, Guid? DuplicateOf = null, double? Overlap = null);

internal sealed record EvidenceDecision(
    EvidenceOutcome Outcome,
    IReadOnlyList<RetrievedChunk> Kept,
    IReadOnlyList<DiscardedPassage> Discarded);

/// <summary>Floor, near-duplicate reduction, cut and outcome over a candidate pool (spec 011, pipeline steps 5–8).</summary>
internal static class EvidenceRules
{
    /// <summary>
    /// SQL orders by distance only (the HNSW index needs it); the tie-break makes equal scores come out the same way every time.
    /// </summary>
    public static List<RetrievedChunk> Rank(IEnumerable<RetrievedChunk> pool) =>
        [.. pool.OrderByDescending(c => c.Score).ThenBy(c => c.DocumentId).ThenBy(c => c.Position)];

    public static EvidenceDecision Apply(IEnumerable<RetrievedChunk> pool, int topK, EvidenceThresholds thresholds)
    {
        var ranked = Rank(pool);

        var discarded = new List<DiscardedPassage>();
        var kept = new List<(RetrievedChunk Passage, HashSet<string> Shingles)>();
        foreach (var candidate in ranked)
        {
            if (candidate.Score < thresholds.MinScore)
            {
                discarded.Add(new DiscardedPassage(candidate, DiscardReason.BelowMinScore));
                continue;
            }

            // Past the cut nothing is compared any more; such candidates are simply not part of the result (spec 011, endpoint).
            if (kept.Count == topK)
            {
                continue;
            }

            var shingles = NearDuplicates.Shingles(candidate.Text);
            var duplicate = kept
                .Select(k => (k.Passage, Overlap: NearDuplicates.Jaccard(shingles, k.Shingles)))
                .Where(k => k.Overlap >= thresholds.DuplicateOverlap)
                .Select(k => ((RetrievedChunk Passage, double Overlap)?)k)
                .FirstOrDefault();

            if (duplicate is { } match)
            {
                discarded.Add(new DiscardedPassage(candidate, DiscardReason.Duplicate, match.Passage.PassageId, match.Overlap));
            }
            else
            {
                kept.Add((candidate, shingles));
            }
        }

        var outcome = kept.Count == 0 ? EvidenceOutcome.NoEvidence
            : kept[0].Passage.Score < thresholds.ConfidentScore ? EvidenceOutcome.LowConfidence
            : EvidenceOutcome.Sufficient;

        return new EvidenceDecision(outcome, [.. kept.Select(k => k.Passage)], discarded);
    }
}
