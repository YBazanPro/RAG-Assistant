using RagAssistant.SharedKernel.Common;

namespace RagAssistant.SharedKernel.Contracts.Documents;

/// <summary>Retrieval over the active, indexed corpus (spec 011). Implemented by the Documents module.</summary>
public interface IDocumentSearch
{
    /// <summary>
    /// Ranked evidence for a question. Fails with <c>validation.failed</c> for invalid input and with
    /// <c>retrieval.embedding_unavailable</c> when the embedding model cannot answer; never throws for either.
    /// </summary>
    Task<Result<SearchResult>> SearchAsync(string query, SearchFilters filters, int topK, CancellationToken cancellationToken);
}
