using RagAssistant.Modules.Documents.Retrieval;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Tests;

// Spec 019: reciprocal rank fusion (AC-9), the hybrid order with stage data, the rerank order, and the gate on an ordered pool.
internal sealed class HybridRankingTests
{
    private static readonly Guid Doc = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private static RetrievedChunk Chunk(double cosine, string text, int position = 0) =>
        new(Guid.NewGuid(), Doc, "Doc", "en", Guid.NewGuid(), 1, [], position, null, null, text, cosine);

    [Test]
    [Arguments(1, 1)]
    [Arguments(3, 7)]
    [Arguments(10, 2)]
    public async Task Fuse_withBothRanks_isTheSumOfReciprocals(int vectorRank, int keywordRank)
    {
        await Assert.That(HybridRanking.Fuse(60, vectorRank, keywordRank)).IsEqualTo((1.0 / (60 + vectorRank)) + (1.0 / (60 + keywordRank)));
    }

    [Test]
    public async Task Fuse_withOneRank_countsOnlyThatPool()
    {
        await Assert.That(HybridRanking.Fuse(60, 4, null)).IsEqualTo(1.0 / 64);
        await Assert.That(HybridRanking.Fuse(60, null, 2)).IsEqualTo(1.0 / 62);
    }

    [Test]
    public async Task Vector_ranksByCosineAndRecordsVectorStages()
    {
        var low = Chunk(0.6, "low");
        var high = Chunk(0.9, "high");

        var ordered = HybridRanking.Vector([low, high]);

        await Assert.That(ordered.Select(c => c.PassageId).SequenceEqual([high.PassageId, low.PassageId])).IsTrue();
        await Assert.That(ordered[0].Stages).IsEqualTo(new PassageStages(1, 0.9, null, null, null, null, AdmittedBy.Vector));
        await Assert.That(ordered[1].Stages!.VectorRank).IsEqualTo(2);
    }

    [Test]
    public async Task Hybrid_keywordHitLowInTheVectorPool_movesUpAndIsAdmittedByBoth()
    {
        var first = Chunk(0.9, "first");
        var second = Chunk(0.85, "second");
        var third = Chunk(0.8, "third");
        var code = Chunk(0.6, "code BX-204");
        var keywordOnly = Chunk(0.4, "only keywords");
        KeywordHit[] hits = [new(code.PassageId, 0.5, 1.0), new(keywordOnly.PassageId, 0.2, 0.25)];

        var ordered = HybridRanking.Hybrid([first, second, third, code], hits, [keywordOnly], k: 60);

        await Assert.That(ordered[0].PassageId).IsEqualTo(code.PassageId);
        await Assert.That(ordered[0].Stages).IsEqualTo(
            new PassageStages(4, 0.6, 1, 0.5, HybridRanking.Fuse(60, 4, 1), null, AdmittedBy.Both, KeywordCoverage: 1.0));
        var onlyKeywords = ordered.Single(c => c.PassageId == keywordOnly.PassageId);
        await Assert.That(onlyKeywords.Stages!.AdmittedBy).IsEqualTo(AdmittedBy.Keyword);
        await Assert.That(onlyKeywords.Stages!.VectorRank).IsNull();
        await Assert.That(onlyKeywords.Score).IsEqualTo(0.4);
        await Assert.That(onlyKeywords.Stages!.KeywordCoverage).IsEqualTo(0.25);
        await Assert.That(ordered.Single(c => c.PassageId == first.PassageId).Stages!.AdmittedBy).IsEqualTo(AdmittedBy.Vector);
        await Assert.That(ordered.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Hybrid_noKeywordHits_keepsTheVectorOrder()
    {
        RetrievedChunk[] pool = [Chunk(0.7, "a"), Chunk(0.9, "b"), Chunk(0.8, "c")];

        var hybrid = HybridRanking.Hybrid(pool, [], [], k: 60);
        var vector = HybridRanking.Vector(pool);

        await Assert.That(hybrid.Select(c => c.PassageId).SequenceEqual(vector.Select(c => c.PassageId))).IsTrue();
    }

    [Test]
    public async Task Rerank_ordersByTheRerankScoreAndMakesItTheGatingScore()
    {
        var ordered = HybridRanking.Vector([Chunk(0.9, "a"), Chunk(0.8, "b"), Chunk(0.7, "c")]);

        var reranked = HybridRanking.Rerank(ordered, [0.1, 0.5, 0.9]);

        await Assert.That(reranked.Select(c => c.Text).SequenceEqual(["c", "b", "a"])).IsTrue();
        await Assert.That(reranked[0].Score).IsEqualTo(0.9);
        await Assert.That(reranked[0].Stages!.RerankScore).IsEqualTo(0.9);
        await Assert.That(reranked[0].Stages!.Cosine).IsEqualTo(0.7);
        await Assert.That(reranked[0].Stages!.VectorRank).IsEqualTo(3);
    }

    [Test]
    public async Task ApplyOrdered_keepsTheGivenOrder_andTheOutcomeUsesTheBestKeptScore()
    {
        var thresholds = new EvidenceThresholds(MinScore: 0.5, ConfidentScore: 0.7, DuplicateOverlap: 0.8);
        var fusedFirst = Chunk(0.6, "keyword match about budget codes");
        var best = Chunk(0.75, "the director approves exceptions");

        var decision = EvidenceRules.ApplyOrdered([fusedFirst, best], topK: 5, thresholds);

        await Assert.That(decision.Kept.SequenceEqual([fusedFirst, best])).IsTrue();
        await Assert.That(decision.Outcome).IsEqualTo(EvidenceOutcome.Sufficient);
    }

    [Test]
    public async Task ApplyOrdered_passageAdmittedPastTheFloor_isKeptButLeavesTheEvidenceLowConfidence()
    {
        var thresholds = new EvidenceThresholds(MinScore: 0.5, ConfidentScore: 0.7, DuplicateOverlap: 0.8);
        var keywordMatch = Chunk(0.3, "budget code BX-204 approval");
        var weak = Chunk(0.3, "nothing in common");

        var decision = EvidenceRules.ApplyOrdered([keywordMatch, weak], topK: 5, thresholds, pastFloor: new HashSet<Guid> { keywordMatch.PassageId });

        await Assert.That(decision.Kept.SequenceEqual([keywordMatch])).IsTrue();
        await Assert.That(decision.Discarded.Single().Reason).IsEqualTo(DiscardReason.BelowMinScore);
        await Assert.That(decision.Outcome).IsEqualTo(EvidenceOutcome.LowConfidence);
    }
}
