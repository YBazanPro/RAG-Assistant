using System.Collections.Concurrent;
using RagAssistant.SharedKernel.Contracts.Retrieval;

namespace RagAssistant.Integration.Tests.Fixtures;

/// <summary>
/// The deterministic reranker of the integration tests (spec 019): scores come from <see cref="Score"/>, a function of the query and
/// the text sent (which starts with the document title, then the passage). Without a score function it fails like an unreachable TEI.
/// </summary>
public sealed class FakeReranker : IPassageReranker
{
    private readonly ConcurrentQueue<IReadOnlyList<string>> _calls = new();

    public string? ModelId { get; set; } = "fake-reranker";

    /// <summary>Scores one text for the query; null makes every call fail.</summary>
    public Func<string, string, double>? Score { get; set; }

    /// <summary>Held this long before answering, to run into the rerank timeout.</summary>
    public TimeSpan? Delay { get; set; }

    /// <summary>Answers this many scores whatever was sent, to test the count check.</summary>
    public int? ReturnedCount { get; set; }

    /// <summary>The texts of every call, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Calls => [.. _calls];

    public async Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        _calls.Enqueue([.. texts]);
        if (Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (Score is not { } score)
        {
            throw new HttpRequestException("The reranker is unreachable in this test.");
        }

        var scores = texts.Select(text => score(query, text)).ToList();
        return ReturnedCount is { } count ? [.. scores.Take(count)] : scores;
    }
}
