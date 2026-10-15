using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Retrieval;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class QueryEmbedderTests
{
    private const int Dimensions = 1024;

    private static (QueryEmbedder Embedder, ServiceProvider Provider) Create(
        IEmbeddingGenerator<string, Embedding<float>> generator, TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        var provider = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        var embedder = new QueryEmbedder(
            generator,
            provider.GetRequiredService<HybridCache>(),
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { QueryEmbeddingTimeout = timeout ?? TimeSpan.FromSeconds(10) }),
            Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions { Model = "test-model" }),
            clock ?? TimeProvider.System,
            NullLogger<QueryEmbedder>.Instance);
        return (embedder, provider);
    }

    [Test]
    [Arguments("  approval   threshold\n", "approval threshold")]
    [Arguments("Approval\tThreshold", "Approval Threshold")]
    public async Task Normalize_collapsesWhitespaceAndKeepsCase(string query, string expected)
    {
        await Assert.That(QueryEmbedder.Normalize(query)).IsEqualTo(expected);
    }

    [Test]
    public async Task CacheKey_hashesTheTextAndIncludesTheModel()
    {
        var key = QueryEmbedder.CacheKey("bge-m3", new string('x', 1000));

        await Assert.That(key).StartsWith("documents:query-embedding:bge-m3:");
        await Assert.That(key.Length).IsLessThan(200);
        await Assert.That(QueryEmbedder.CacheKey("other", "q")).IsNotEqualTo(QueryEmbedder.CacheKey("bge-m3", "q"));
    }

    [Test]
    public async Task EmbedAsync_sameNormalizedQuery_callsTheModelOnce()
    {
        var generator = new CountingGenerator();
        var (embedder, provider) = Create(generator);
        await using var _ = provider;

        var first = await embedder.EmbedAsync("approval threshold", CancellationToken.None);
        var second = await embedder.EmbedAsync("  approval    threshold ", CancellationToken.None);

        await Assert.That(generator.Calls).IsEqualTo(1);
        await Assert.That(first.Value.CacheHit).IsFalse();
        await Assert.That(second.Value.CacheHit).IsTrue();
        await Assert.That(second.Value.Vector.Length).IsEqualTo(Dimensions);
    }

    [Test]
    public async Task EmbedAsync_differentCase_callsTheModelAgain()
    {
        var generator = new CountingGenerator();
        var (embedder, provider) = Create(generator);
        await using var _ = provider;

        await embedder.EmbedAsync("approval threshold", CancellationToken.None);
        await embedder.EmbedAsync("Approval threshold", CancellationToken.None);

        await Assert.That(generator.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task EmbedAsync_modelFails_isUnavailableAndNotCached()
    {
        var generator = new CountingGenerator { Fail = true };
        var (embedder, provider) = Create(generator);
        await using var _ = provider;

        var failed = await embedder.EmbedAsync("approval threshold", CancellationToken.None);
        generator.Fail = false;
        var recovered = await embedder.EmbedAsync("approval threshold", CancellationToken.None);

        await Assert.That(failed.IsFailure).IsTrue();
        await Assert.That(failed.Error.Code).IsEqualTo(QueryEmbedder.UnavailableCode);
        await Assert.That(failed.Error.Type).IsEqualTo(ErrorType.Unavailable);
        await Assert.That(recovered.IsSuccess).IsTrue();
        await Assert.That(generator.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task EmbedAsync_wrongVectorSize_isUnavailable()
    {
        var (embedder, provider) = Create(new CountingGenerator { Length = 8 });
        await using var _ = provider;

        var result = await embedder.EmbedAsync("approval threshold", CancellationToken.None);

        await Assert.That(result.Error.Code).IsEqualTo(QueryEmbedder.UnavailableCode);
    }

    [Test]
    public async Task EmbedAsync_modelHangs_timesOutAsUnavailable()
    {
        var clock = new ManualTimeProvider();
        var generator = new CountingGenerator { Hang = true };
        var (embedder, provider) = Create(generator, clock, TimeSpan.FromSeconds(10));
        await using var _ = provider;

        var pending = embedder.EmbedAsync("approval threshold", CancellationToken.None);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(result.Error.Code).IsEqualTo(QueryEmbedder.UnavailableCode);
    }

    [Test]
    public async Task EmbedAsync_callerCancels_throws()
    {
        var generator = new CountingGenerator { Hang = true };
        var (embedder, provider) = Create(generator);
        await using var _ = provider;
        using var cancellation = new CancellationTokenSource();

        var pending = embedder.EmbedAsync("approval threshold", cancellation.Token);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _calls;

        public int Calls => _calls;

        public bool Fail { get; set; }

        public bool Hang { get; init; }

        public int Length { get; init; } = Dimensions;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Fail)
            {
                throw new HttpRequestException("Scripted failure.");
            }

            return new([.. values.Select(_ => new Embedding<float>(Enumerable.Repeat(0.1f, Length).ToArray()))]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
