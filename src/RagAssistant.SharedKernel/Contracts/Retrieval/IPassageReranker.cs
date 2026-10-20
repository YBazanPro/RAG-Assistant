namespace RagAssistant.SharedKernel.Contracts.Retrieval;

/// <summary>
/// A cross-encoder that scores how well each text answers a query (spec 019). Implemented by the API, which alone knows the provider
/// (Text Embeddings Inference, ADR 0010); the Documents module only sees this interface.
/// </summary>
public interface IPassageReranker
{
    /// <summary>The model that scores, as configured; null when no reranker is configured.</summary>
    string? ModelId { get; }

    /// <summary>
    /// One relevance score per text, in the order of <paramref name="texts"/>, higher is better. Throws when the reranker is not
    /// configured or not reachable; the caller decides what a failure means (spec 019: retrieval continues without reranking).
    /// </summary>
    Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
