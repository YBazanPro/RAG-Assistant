using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Retrieval;

internal sealed record EmbeddedQuery(float[] Vector, bool CacheHit);

/// <summary>Embeds a question once per model and normalized text, behind HybridCache (spec 011, pipeline step 2; ADR 0008).</summary>
internal sealed partial class QueryEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    HybridCache cache,
    IOptions<RetrievalOptions> retrieval,
    IOptions<EmbeddingOptions> embeddings,
    TimeProvider clock,
    ILogger<QueryEmbedder> logger)
{
    public const string UnavailableCode = "retrieval.embedding_unavailable";

    public string ModelId => PassageEmbedder.ResolveModelId(generator, embeddings.Value);

    /// <summary>Trimmed, whitespace runs collapsed to one space; case is kept.</summary>
    public static string Normalize(string query) =>
        string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The key hashes the text: it stays under HybridCache's key limit and keeps questions out of the keys (ADR 0008).</summary>
    public static string CacheKey(string model, string normalizedQuery) =>
        $"documents:query-embedding:{model}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedQuery)))}";

    public async Task<Result<EmbeddedQuery>> EmbedAsync(string query, CancellationToken cancellationToken)
    {
        var options = retrieval.Value;
        var normalized = Normalize(query);
        var computed = false;
        try
        {
            var vector = await cache.GetOrCreateAsync(
                CacheKey(ModelId, normalized),
                async token =>
                {
                    computed = true;
                    return await GenerateAsync(normalized, options.QueryEmbeddingTimeout, token);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = options.QueryEmbeddingCacheDuration,
                    LocalCacheExpiration = options.QueryEmbeddingCacheDuration,
                },
                cancellationToken: cancellationToken);
            return new EmbeddedQuery(vector, CacheHit: !computed);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Spec 011: any failure of the model other than the caller's own cancellation (unreachable, not pulled, timeout,
            // wrong vector size) is "embedding unavailable", never "no evidence". A throwing factory is not cached.
            LogUnavailable(logger, ex.GetType().Name, ex);
            return Error.Unavailable(UnavailableCode, "The embedding model is not available. Try again later.");
        }
    }

    private async Task<float[]> GenerateAsync(string text, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var result = await generator.GenerateAsync([text], cancellationToken: linked.Token);

        var dimensions = DocumentsDbContext.EmbeddingDimensions;
        if (result.Count != 1 || result[0].Vector.Length != dimensions)
        {
            throw new InvalidOperationException($"The embedding model returned {result.Count} vector(s), expected one of size {dimensions}.");
        }

        return result[0].Vector.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query embedding failed ({ErrorType}); the search reports the model as unavailable.")]
    private static partial void LogUnavailable(ILogger logger, string errorType, Exception exception);
}
