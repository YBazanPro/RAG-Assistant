namespace RagAssistant.SharedKernel.Contracts.Documents;

/// <summary>Which documents exist and which can be searched right now (spec 015). Implemented by the Documents module.</summary>
public interface IDocumentCatalog
{
    Task<DocumentAvailability> CheckAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
}

/// <param name="Known">The requested ids that name a document, whatever its state.</param>
/// <param name="Searchable">The subset that is active with an indexed version, so retrieval can return its passages.</param>
/// <param name="Titles">The current title of each known document.</param>
public sealed record DocumentAvailability(IReadOnlySet<Guid> Known, IReadOnlySet<Guid> Searchable, IReadOnlyDictionary<Guid, string> Titles);
