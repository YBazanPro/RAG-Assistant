using Microsoft.Extensions.AI;

namespace RagAssistant.Integration.Tests.Fixtures;

/// <summary>The default test generator: always unreachable, so the ingestion worker stays paused and imported documents stay queued.</summary>
internal sealed class UnavailableEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("The embedding model is unavailable in this test.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
