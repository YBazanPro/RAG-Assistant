using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <summary>Answers, for a set of ids, which documents exist and which retrieval can use (spec 015).</summary>
internal sealed class DocumentCatalog(DocumentsDbContext db) : IDocumentCatalog
{
    public async Task<DocumentAvailability> CheckAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count == 0)
        {
            return new DocumentAvailability(new HashSet<Guid>(), new HashSet<Guid>());
        }

        var wanted = documentIds.Distinct().ToArray();
        var rows = await db.Documents.AsNoTracking()
            .Where(d => wanted.Contains(d.Id))
            .Select(d => new { d.Id, Searchable = d.Status == DocumentStatus.Active && d.ActiveVersionId != null })
            .ToListAsync(cancellationToken);

        return new DocumentAvailability(
            rows.Select(r => r.Id).ToHashSet(),
            rows.Where(r => r.Searchable).Select(r => r.Id).ToHashSet());
    }
}
