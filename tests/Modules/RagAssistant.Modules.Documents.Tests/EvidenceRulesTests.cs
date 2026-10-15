using RagAssistant.Modules.Documents.Retrieval;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class EvidenceRulesTests
{
    private static readonly EvidenceThresholds Thresholds = new(MinScore: 0.5, ConfidentScore: 0.7, DuplicateOverlap: 0.8);

    private static readonly Guid DocA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid DocB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static RetrievedChunk Chunk(double score, string text, Guid? document = null, int position = 0) =>
        new(Guid.NewGuid(), document ?? DocA, "Doc", "en", Guid.NewGuid(), 1, [], position, null, null, text, score);

    [Test]
    public async Task Apply_belowMinScore_isDiscardedAndOthersRankedByScore()
    {
        var low = Chunk(0.49, "budget approval limits for travel");
        var mid = Chunk(0.6, "escalation rules for finance managers");
        var high = Chunk(0.9, "exceptions need director sign off");

        var decision = EvidenceRules.Apply([low, mid, high], topK: 5, Thresholds);

        await Assert.That(decision.Kept.SequenceEqual([high, mid])).IsTrue();
        await Assert.That(decision.Discarded.Count).IsEqualTo(1);
        await Assert.That(decision.Discarded[0].Passage).IsEqualTo(low);
        await Assert.That(decision.Discarded[0].Reason).IsEqualTo(DiscardReason.BelowMinScore);
    }

    [Test]
    public async Task Apply_noCandidateAboveFloor_isNoEvidence()
    {
        var decision = EvidenceRules.Apply([Chunk(0.1, "a b c"), Chunk(0.49, "d e f")], topK: 5, Thresholds);

        await Assert.That(decision.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        await Assert.That(decision.Kept).IsEmpty();
        await Assert.That(decision.Discarded.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Apply_emptyPool_isNoEvidence()
    {
        var decision = EvidenceRules.Apply([], topK: 5, Thresholds);

        await Assert.That(decision.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        await Assert.That(decision.Discarded).IsEmpty();
    }

    [Test]
    [Arguments(0.5, EvidenceOutcome.LowConfidence)]
    [Arguments(0.6999, EvidenceOutcome.LowConfidence)]
    [Arguments(0.7, EvidenceOutcome.Sufficient)]
    [Arguments(0.95, EvidenceOutcome.Sufficient)]
    public async Task Apply_bestScore_decidesOutcome(double best, EvidenceOutcome expected)
    {
        var decision = EvidenceRules.Apply([Chunk(best, "one two three"), Chunk(0.5, "four five six")], topK: 5, Thresholds);

        await Assert.That(decision.Outcome).IsEqualTo(expected);
    }

    [Test]
    public async Task Apply_nearDuplicate_keepsHigherAndRecordsOverlap()
    {
        const string text = "Budget exceptions above ten thousand dollars need approval from the finance director.";
        var original = Chunk(0.9, text, DocA);
        var copy = Chunk(0.85, text + " ", DocB);

        var decision = EvidenceRules.Apply([copy, original], topK: 5, Thresholds);

        await Assert.That(decision.Kept.SequenceEqual([original])).IsTrue();
        var discarded = decision.Discarded.Single();
        await Assert.That(discarded.Passage).IsEqualTo(copy);
        await Assert.That(discarded.Reason).IsEqualTo(DiscardReason.Duplicate);
        await Assert.That(discarded.DuplicateOf).IsEqualTo(original.PassageId);
        await Assert.That(discarded.Overlap).IsEqualTo(1.0);
    }

    [Test]
    public async Task Apply_overlapBelowRatio_keepsBoth()
    {
        // Neighbouring chunks sharing two words: no 3-gram in common.
        var first = Chunk(0.9, "alpha beta gamma delta epsilon zeta eta theta");
        var second = Chunk(0.8, "eta theta iota kappa lambda mu nu xi");

        var decision = EvidenceRules.Apply([first, second], topK: 5, Thresholds);

        await Assert.That(decision.Kept.Count).IsEqualTo(2);
        await Assert.That(decision.Discarded).IsEmpty();
    }

    [Test]
    public async Task Apply_duplicatesInTop_refillsFromPool()
    {
        const string text = "same words repeated in two passages of the corpus";
        var pool = new[]
        {
            Chunk(0.95, text, DocA, 0),
            Chunk(0.94, text, DocB, 0),
            Chunk(0.90, "a distinct passage about travel rules", DocA, 1),
            Chunk(0.85, "another distinct passage about escalation", DocA, 2),
        };

        var decision = EvidenceRules.Apply(pool, topK: 3, Thresholds);

        await Assert.That(decision.Kept.Select(k => k.Score).SequenceEqual([0.95, 0.90, 0.85])).IsTrue();
    }

    [Test]
    public async Task Apply_beyondTopK_isNeitherKeptNorDiscarded()
    {
        var pool = Enumerable.Range(0, 6).Select(i => Chunk(0.9 - (i * 0.01), $"distinct passage number {i} words", position: i)).ToArray();

        var decision = EvidenceRules.Apply(pool, topK: 2, Thresholds);

        await Assert.That(decision.Kept.Count).IsEqualTo(2);
        await Assert.That(decision.Discarded).IsEmpty();
    }

    [Test]
    public async Task Apply_equalScores_orderByDocumentThenPosition()
    {
        var b0 = Chunk(0.8, "one alpha words here", DocB, 0);
        var a2 = Chunk(0.8, "two beta words here", DocA, 2);
        var a1 = Chunk(0.8, "three gamma words here", DocA, 1);

        var first = EvidenceRules.Apply([b0, a2, a1], topK: 5, Thresholds);
        var second = EvidenceRules.Apply([a1, b0, a2], topK: 5, Thresholds);

        RetrievedChunk[] expected = [a1, a2, b0];
        await Assert.That(first.Kept.SequenceEqual(expected)).IsTrue();
        await Assert.That(second.Kept.SequenceEqual(expected)).IsTrue();
    }
}
