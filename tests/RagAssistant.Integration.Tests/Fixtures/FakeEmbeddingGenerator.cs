using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace RagAssistant.Integration.Tests.Fixtures;

/// <summary>
/// Deterministic, scriptable stand-in for the embedding model. The worker's availability probe (a single "ping") is told apart
/// from document batches, so a test can keep the model "reachable" while its batches fail.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ConcurrentQueue<string[]> _batches = [];
    private readonly ConcurrentQueue<(string Keyword, float[] Vector)> _scripted = [];
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _gate;
    private int _failuresRemaining;
    private volatile bool _available = true;
    private volatile bool _alwaysFail;
    private volatile bool _throwUnexpected;

    /// <summary>The readiness switch: false makes the probe fail, so the worker pauses.</summary>
    public bool Available
    {
        get => _available;
        set => _available = value;
    }

    /// <summary>Every batch fails with a transient error.</summary>
    public bool AlwaysFail
    {
        get => _alwaysFail;
        set => _alwaysFail = value;
    }

    /// <summary>Every batch throws an exception the pipeline does not expect.</summary>
    public bool ThrowUnexpected
    {
        get => _throwUnexpected;
        set => _throwUnexpected = value;
    }

    public int VectorLength { get; set; } = 1024;

    /// <summary>What the provider reports as its model; null behaves like a client without metadata.</summary>
    public string? ReportedModel { get; set; }

    /// <summary>When set, batches return this many vectors instead of one per input.</summary>
    public int? ReturnedCount { get; set; }

    public IReadOnlyList<string[]> Batches => [.. _batches];

    /// <summary>A unit vector along one axis; with <see cref="WithScore"/> it gives passages an exact similarity to a query.</summary>
    public static float[] Axis(int axis, int length = 1024)
    {
        var vector = new float[length];
        vector[axis] = 1f;
        return vector;
    }

    /// <summary>A unit vector whose cosine similarity to <see cref="Axis"/>(0) is <paramref name="score"/>; the rest lies on <paramref name="otherAxis"/>.</summary>
    public static float[] WithScore(double score, int otherAxis = 1, int length = 1024)
    {
        var vector = new float[length];
        vector[0] = (float)score;
        vector[otherAxis] = (float)Math.Sqrt(1 - (score * score));
        return vector;
    }

    /// <summary>
    /// Like <see cref="WithScore"/>, but the remainder points in a random direction of a shared subspace (axes 1..<paramref name="dimensions"/>),
    /// so passages have varied similarities to each other, as real embeddings do, and an HNSW graph over them is well connected.
    /// </summary>
    public static float[] WithScore(double score, Random random, int dimensions = 32, int length = 1024)
    {
        var direction = new double[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            direction[i] = random.NextDouble() - 0.5;
        }

        var norm = Math.Sqrt(direction.Sum(d => d * d));
        var rest = Math.Sqrt(1 - (score * score));
        var vector = new float[length];
        vector[0] = (float)score;
        for (var i = 0; i < dimensions; i++)
        {
            vector[1 + i] = (float)(rest * direction[i] / norm);
        }

        return vector;
    }

    /// <summary>Any input containing <paramref name="keyword"/> (case-insensitive) embeds as <paramref name="vector"/>; the first match wins.</summary>
    public void Script(string keyword, float[] vector) => _scripted.Enqueue((keyword, vector));

    public int BatchCalls => _batches.Count;

    /// <summary>The next <paramref name="count"/> batches fail with a transient error, then calls succeed again.</summary>
    public void FailNext(int count) => Volatile.Write(ref _failuresRemaining, count);

    /// <summary>Batches block until <see cref="Release"/> is called; lets a test observe a run in progress.</summary>
    public void HoldBatches() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilHeldAsync() => _held.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public void Release() => _gate?.TrySetResult();

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values.ToArray();
        if (inputs is ["ping"])
        {
            return Available
                ? Vectors(inputs.Length)
                : throw new HttpRequestException("The embedding model is unavailable in this test.");
        }

        _batches.Enqueue(inputs);

        if (ThrowUnexpected)
        {
            throw new InvalidOperationException("Scripted unexpected failure.");
        }

        if (AlwaysFail || TakeScriptedFailure())
        {
            throw new HttpRequestException("Scripted transient failure.");
        }

        if (_gate is { } gate)
        {
            _held.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        return Vectors(ReturnedCount ?? inputs.Length, inputs);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) && ReportedModel is not null
            ? new EmbeddingGeneratorMetadata("fake", defaultModelId: ReportedModel)
            : null;

    public void Dispose()
    {
    }

    private bool TakeScriptedFailure()
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _failuresRemaining);
            if (remaining <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _failuresRemaining, remaining - 1, remaining) == remaining)
            {
                return true;
            }
        }
    }

    private GeneratedEmbeddings<Embedding<float>> Vectors(int count, string[]? inputs = null) =>
        new(Enumerable.Range(0, count).Select(i => new Embedding<float>(Vector(inputs is null || i >= inputs.Length ? string.Empty : inputs[i]))));

    private float[] Vector(string text)
    {
        foreach (var (keyword, scripted) in _scripted)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return [.. scripted];
            }
        }

        var seed = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
        var vector = new float[VectorLength];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (((seed >> (i % 24)) & 0xFF) / 255f) + 0.01f;
        }

        return vector;
    }
}
