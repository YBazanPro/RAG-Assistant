using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.SharedKernel.Contracts.Documents;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 019, reranking: AC-10, AC-12 to AC-14 (AC-11 is in Chat/RetrievalStagesTests).
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class RerankingTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> Reranking = new()
    {
        ["Documents:Retrieval:Reranking:Enabled"] = "true",
        ["Documents:Retrieval:Reranking:MinScore"] = "0.2",
        ["Documents:Retrieval:Reranking:ConfidentScore"] = "0.5",
        ["Documents:Retrieval:Reranking:Timeout"] = "00:00:01",
    };

    /// <summary>The number written in a passage's text ("rank 3"), so the fake reranker can score by it.</summary>
    private static int RankIn(string text) => int.Parse(text[(text.LastIndexOf("rank ", StringComparison.Ordinal) + 5)..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    // AC-10
    [Test]
    public async Task Search_rerankerReversingTheOrder_followsItReducesDuplicatesAfterAndGatesOnItsScore()
    {
        await using var bed = await StartAsync(postgres, Reranking);
        await bed.AddAsync(
            "Policy",
            P(0.9, "Exceptions need the department head. rank 1"),
            P(0.8, "Finance signs off on budget changes. rank 2"),
            P(0.7, "Travel above the limit needs approval from the director. rank 3"),
            P(0.65, "Travel above the limit needs approval from the director. rank 3 copy"));
        // Lower cosine scores higher: the reverse of the vector order. The two "rank 3" passages are near-duplicates.
        bed.App.Reranker.Score = (_, text) => RankIn(text) / 4.0;

        var result = await bed.SearchAsync();

        var chunks = result.Value.Chunks;
        await Assert.That(result.Value.Rerank).IsEqualTo(RerankStatus.Applied);
        await Assert.That(chunks.Select(c => RankIn(c.Text)).SequenceEqual([3, 2, 1])).IsTrue();
        await Assert.That(chunks[0].Score).IsEqualTo(0.75);
        await Assert.That(chunks[0].Stages!.RerankScore).IsEqualTo(0.75);
        await Assert.That(Math.Round(chunks[0].Stages!.Cosine, 4)).IsEqualTo(0.7);
        await Assert.That(result.Value.Thresholds.MinScore).IsEqualTo(0.2);
        await Assert.That(result.Value.Outcome).IsEqualTo(EvidenceOutcome.Sufficient);
        await Assert.That(result.Value.RerankModel).IsEqualTo("fake-reranker");
    }

    [Test]
    public async Task Search_rerankScoresBelowItsFloor_isNoEvidenceEvenWithHighCosine()
    {
        await using var bed = await StartAsync(postgres, Reranking);
        await bed.AddAsync("Policy", P(0.95), P(0.9, otherAxis: 2));
        bed.App.Reranker.Score = (_, _) => 0.05;

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        await Assert.That(result.Value.Rerank).IsEqualTo(RerankStatus.Applied);
    }

    // AC-12 and AC-14
    [Test]
    [Arguments("timeout")]
    [Arguments("unavailable")]
    [Arguments("wrong_count")]
    public async Task Search_rerankerFailing_fallsBackToCosineAndCountsTheSkip(string failure)
    {
        await using var bed = await StartAsync(postgres, Reranking);
        await bed.AddAsync("Policy", P(0.9, "first rank 1"), P(0.6, "second rank 2", otherAxis: 2));
        switch (failure)
        {
            case "timeout":
                bed.App.Reranker.Score = (_, _) => 0.9;
                bed.App.Reranker.Delay = TimeSpan.FromSeconds(5);
                break;
            case "wrong_count":
                bed.App.Reranker.Score = (_, _) => 0.9;
                bed.App.Reranker.ReturnedCount = 1;
                break;
        }

        var skipped = new ConcurrentBag<string>();
        var meterFactory = bed.App.Services.GetRequiredService<IMeterFactory>();
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "documents.rerank.skipped" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    skipped.Add(tag.Value as string ?? string.Empty);
                }
            }
        });
        meters.Start();

        var result = await bed.SearchAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Rerank).IsEqualTo(RerankStatus.Skipped);
        await Assert.That(result.Value.Chunks.Select(c => Math.Round(c.Score, 4)).SequenceEqual([0.9, 0.6])).IsTrue();
        await Assert.That(result.Value.Thresholds.MinScore).IsEqualTo(0.5);
        await Assert.That(result.Value.Chunks.All(c => c.Stages!.RerankScore is null)).IsTrue();
        await Assert.That(skipped.ToArray()).IsEquivalentTo([failure]);
    }

    // AC-13
    [Test]
    public async Task Search_poolLargerThanTheCandidates_sendsOnlyTheBestCandidatesInOneCall()
    {
        await using var bed = await StartAsync(postgres, new Dictionary<string, string>(Reranking)
        {
            ["Documents:Retrieval:CandidateMultiplier"] = "5",
        });
        var random = new Random(19);
        await bed.AddAsync("Policy", [.. Enumerable.Range(0, 100).Select(i => new SeedPassage($"passage {i} {Guid.NewGuid()}", FakeEmbeddingGenerator.WithScore(0.6 + (i / 400.0), random)))]);
        bed.App.Reranker.Score = (_, _) => 0.5;

        var result = await bed.SearchAsync(topK: 20);

        var call = bed.App.Reranker.Calls.Single();
        await Assert.That(call.Count).IsEqualTo(30);
        await Assert.That(result.Value.Chunks.Count).IsEqualTo(20);
    }
}
