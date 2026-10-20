using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class PassageEmbedderTests
{
    private const int Dimensions = 4;

    private static PassageEmbedder Create(
        ScriptedGenerator generator,
        int batchSize = 3,
        int maxRetries = 3,
        TimeSpan? timeout = null,
        TimeProvider? clock = null,
        TimeSpan? retryDelay = null) =>
        new(
            generator,
            Microsoft.Extensions.Options.Options.Create(new IngestionOptions
            {
                EmbeddingBatchSize = batchSize,
                EmbeddingMaxRetries = maxRetries,
                EmbeddingRetryBaseDelay = retryDelay ?? TimeSpan.FromMilliseconds(1),
                EmbeddingTimeout = timeout ?? TimeSpan.FromSeconds(30),
            }),
            Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions { Dimensions = Dimensions, Model = "test-model" }),
            clock ?? TimeProvider.System,
            NullLogger<PassageEmbedder>.Instance);

    [Test]
    public async Task Build_emptyPath_startsWithTheTitleAlone()
    {
        await Assert.That(EmbeddingInput.Build("IT Equipment Policy", [], "body")).IsEqualTo("IT Equipment Policy\n\nbody");
    }

    [Test]
    public async Task Build_withPath_putsTheTitleBeforeTheHeadingsAndSeparatesWithBlankLine()
    {
        await Assert.That(EmbeddingInput.Build("Policy", ["A", "B"], "body")).IsEqualTo("Policy > A > B\n\nbody");
    }

    [Test]
    [Arguments("Budget Exception Policy")]
    [Arguments(" budget exception policy ")]
    public async Task Build_firstHeadingEqualToTheTitle_writesTheTitleOnce(string title)
    {
        await Assert.That(EmbeddingInput.Build(title, ["Budget Exception Policy", "Approval"], "body"))
            .IsEqualTo("Budget Exception Policy > Approval\n\nbody");
    }

    [Test]
    public async Task Build_blankTitleAndNoPath_returnsTextOnly()
    {
        await Assert.That(EmbeddingInput.Build(" ", [], "body")).IsEqualTo("body");
    }

    [Test]
    public async Task EmbedAsync_moreInputsThanBatchSize_callsInBatchesAndKeepsOrder()
    {
        var generator = new ScriptedGenerator();
        var inputs = Enumerable.Range(0, 7).Select(n => $"text {n}").ToArray();

        var vectors = await Create(generator, batchSize: 3).EmbedAsync(inputs, CancellationToken.None);

        await Assert.That(generator.BatchSizes.ToArray()).IsEquivalentTo([3, 3, 1]);
        await Assert.That(vectors.Count).IsEqualTo(7);
        await Assert.That(vectors.Select(v => v[0]).ToArray()).IsEquivalentTo(Enumerable.Range(0, 7).Select(n => (float)n).ToArray());
    }

    [Test]
    public async Task EmbedAsync_failsTwiceThenSucceeds_retries()
    {
        var generator = new ScriptedGenerator { FailuresBeforeSuccess = 2 };

        var vectors = await Create(generator).EmbedAsync(["a", "b"], CancellationToken.None);

        await Assert.That(vectors.Count).IsEqualTo(2);
        await Assert.That(generator.Calls).IsEqualTo(3);
    }

    [Test]
    public async Task EmbedAsync_transientFailures_backOffExponentiallyOnTheClock()
    {
        var clock = new ManualTimeProvider();
        var generator = new ScriptedGenerator { FailuresBeforeSuccess = 2 };
        var embedder = Create(generator, clock: clock, retryDelay: TimeSpan.FromSeconds(2));

        var embedding = embedder.EmbedAsync(["a"], CancellationToken.None);

        // First failure: wait base delay (2 s). Nothing happens until the clock gets there.
        await WaitUntilAsync(() => generator.Calls == 1 && clock.PendingTimers == 1);
        clock.Advance(TimeSpan.FromMilliseconds(1900));
        await Task.Delay(50);
        await Assert.That(generator.Calls).IsEqualTo(1);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        // Second failure: wait twice as long (4 s).
        await WaitUntilAsync(() => generator.Calls == 2 && clock.PendingTimers == 1);
        clock.Advance(TimeSpan.FromMilliseconds(3900));
        await Task.Delay(50);
        await Assert.That(generator.Calls).IsEqualTo(2);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        var vectors = await embedding.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(vectors.Count).IsEqualTo(1);
        await Assert.That(generator.Calls).IsEqualTo(3);
        await Assert.That(clock.Elapsed).IsEqualTo(TimeSpan.FromSeconds(6));
    }

    [Test]
    public async Task EmbedAsync_callOutlastsTheTimeout_failsOnTheClockNotTheWallClock()
    {
        var clock = new ManualTimeProvider();
        var generator = new ScriptedGenerator { Hang = true };
        var embedder = Create(generator, maxRetries: 1, clock: clock, timeout: TimeSpan.FromSeconds(60), retryDelay: TimeSpan.FromSeconds(2));

        var embedding = embedder.EmbedAsync(["a"], CancellationToken.None);
        await WaitUntilAsync(() => generator.Calls == 1);
        await Task.Delay(50);
        await Assert.That(embedding.IsCompleted).IsFalse();

        clock.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => clock.PendingTimers == 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => generator.Calls == 2);
        clock.Advance(TimeSpan.FromSeconds(60));

        await Assert.That(async () => { await embedding.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<IngestionException>();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5);
        }
    }

    [Test]
    public async Task EmbedAsync_alwaysFails_throwsUnavailableAfterMaxRetries()
    {
        var generator = new ScriptedGenerator { FailuresBeforeSuccess = int.MaxValue };

        var exception = await Assert.That(async () => { await Create(generator, maxRetries: 3).EmbedAsync(["a"], CancellationToken.None); })
            .Throws<IngestionException>();

        await Assert.That(exception!.Code).IsEqualTo(IngestionErrorCodes.EmbeddingUnavailable);
        await Assert.That(generator.Calls).IsEqualTo(4);
    }

    [Test]
    public async Task EmbedAsync_hangingCall_timesOutAndCountsAsFailure()
    {
        var generator = new ScriptedGenerator { Hang = true };

        var exception = await Assert.That(async () =>
            {
                await Create(generator, maxRetries: 1, timeout: TimeSpan.FromMilliseconds(50)).EmbedAsync(["a"], CancellationToken.None);
            })
            .Throws<IngestionException>();

        await Assert.That(exception!.Code).IsEqualTo(IngestionErrorCodes.EmbeddingUnavailable);
        await Assert.That(generator.Calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(Dimensions + 1, 2)]
    [Arguments(Dimensions, 1)]
    public async Task EmbedAsync_wrongVectorLengthOrCount_failsAtOnceWithoutRetry(int vectorLength, int returnedCount)
    {
        var generator = new ScriptedGenerator { VectorLength = vectorLength, ReturnedCount = returnedCount };

        var exception = await Assert.That(async () => { await Create(generator).EmbedAsync(["a", "b"], CancellationToken.None); })
            .Throws<IngestionException>();

        await Assert.That(exception!.Code).IsEqualTo(IngestionErrorCodes.EmbeddingDimensionMismatch);
        await Assert.That(generator.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task EmbedAsync_unexpectedException_propagatesWithoutRetry()
    {
        var generator = new ScriptedGenerator { Crash = true };

        await Assert.That(async () => { await Create(generator).EmbedAsync(["a"], CancellationToken.None); })
            .Throws<InvalidOperationException>();
        await Assert.That(generator.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task IsAvailableAsync_modelAnswers_returnsTrue()
    {
        await Assert.That(await Create(new ScriptedGenerator()).IsAvailableAsync(CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task IsAvailableAsync_anyProviderFailure_returnsFalse()
    {
        // A client-specific "model not found" is not one of the transient types, but it still means "not yet".
        await Assert.That(await Create(new ScriptedGenerator { Crash = true }).IsAvailableAsync(CancellationToken.None)).IsFalse();
        await Assert.That(await Create(new ScriptedGenerator { FailuresBeforeSuccess = 1 }).IsAvailableAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task IsAvailableAsync_hangingModel_timesOutAsUnavailable()
    {
        var embedder = Create(new ScriptedGenerator { Hang = true }, timeout: TimeSpan.FromMilliseconds(50));

        await Assert.That(await embedder.IsAvailableAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task IsAvailableAsync_callerCancels_propagatesCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var embedder = Create(new ScriptedGenerator { Hang = true });

        await Assert.That(async () => { await embedder.IsAvailableAsync(cts.Token); }).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ModelId_providerReportsAModel_isPreferredOverTheConfiguredName()
    {
        await Assert.That(Create(new ScriptedGenerator { ReportedModel = "bge-m3:latest" }).ModelId).IsEqualTo("bge-m3:latest");
    }

    [Test]
    public async Task ModelId_providerReportsNothing_fallsBackToTheConfiguredName()
    {
        await Assert.That(Create(new ScriptedGenerator()).ModelId).IsEqualTo("test-model");
    }

    [Test]
    public async Task EmbedAsync_callerCancels_propagatesCancellation()
    {
        var generator = new ScriptedGenerator { Hang = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.That(async () => { await Create(generator).EmbedAsync(["a"], cts.Token); }).Throws<OperationCanceledException>();
        await Assert.That(generator.Calls).IsEqualTo(1);
    }

    private sealed class ScriptedGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _calls;
        private int _sequence;

        public int Calls => _calls;

        public List<int> BatchSizes { get; } = [];

        public int FailuresBeforeSuccess { get; init; }

        public bool Hang { get; init; }

        public bool Crash { get; init; }

        public int VectorLength { get; init; } = Dimensions;

        public int? ReturnedCount { get; init; }

        public string? ReportedModel { get; init; }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            var batch = values.ToArray();
            BatchSizes.Add(batch.Length);

            if (Crash)
            {
                throw new InvalidOperationException("boom");
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (call <= FailuresBeforeSuccess)
            {
                throw new HttpRequestException("connection refused");
            }

            var count = ReturnedCount ?? batch.Length;
            return new GeneratedEmbeddings<Embedding<float>>(
                Enumerable.Range(0, count).Select(_ =>
                {
                    var vector = new float[VectorLength];
                    vector[0] = _sequence++;
                    return new Embedding<float>(vector);
                }));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) && ReportedModel is not null
                ? new EmbeddingGeneratorMetadata("test", defaultModelId: ReportedModel)
                : null;

        public void Dispose()
        {
        }
    }
}
