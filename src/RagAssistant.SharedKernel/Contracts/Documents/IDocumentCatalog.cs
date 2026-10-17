namespace RagAssistant.SharedKernel.Contracts.Documents;

/// <summary>Which documents the viewer can read and which of those can be searched right now (spec 015, 016). Implemented by the Documents module.</summary>
public interface IDocumentCatalog
{
    Task<DocumentAvailability> CheckAsync(IReadOnlyCollection<Guid> documentIds, string viewerSub, CancellationToken cancellationToken);
}

/// <param name="Known">The requested ids that name a document the viewer can read, whatever its state; a document the viewer cannot read is treated like an id that does not exist (spec 016).</param>
/// <param name="Searchable">The subset that is active with an indexed version, so retrieval can return its passages.</param>
/// <param name="Titles">The current title of each known document.</param>
public sealed record DocumentAvailability(IReadOnlySet<Guid> Known, IReadOnlySet<Guid> Searchable, IReadOnlyDictionary<Guid, string> Titles);
