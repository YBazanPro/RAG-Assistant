using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <summary>Titles of shared documents only (spec 017). It filters on <see cref="Visibility.Shared"/> itself, because no viewer is involved.</summary>
internal sealed class DocumentTitles(DocumentsDbContext db) : IDocumentTitles
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetSharedTitlesAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var wanted = documentIds.Distinct().ToArray();
        return await db.Documents.AsNoTracking()
            .Where(d => d.Visibility == Visibility.Shared && wanted.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Title, cancellationToken);
    }
}
