namespace RagAssistant.SharedKernel.Contracts.Documents;

/// <summary>
/// The current titles of documents that every signed-in user can read (spec 017). It takes no viewer, so it can only reveal what is
/// already public; it is meant for maintainer diagnosis, where private titles must stay hidden. Implemented by the Documents module.
/// </summary>
public interface IDocumentTitles
{
    /// <returns>An entry for each requested id that names a shared document, archived or not. Private and unknown ids have no entry, so nothing tells them apart.</returns>
    Task<IReadOnlyDictionary<Guid, string>> GetSharedTitlesAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
}
