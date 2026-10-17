using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <summary>Answers, for a set of ids, which documents exist and which retrieval can use (spec 015).</summary>
internal sealed class DocumentCatalog(DocumentsDbContext db) : IDocumentCatalog
{
    public async Task<DocumentAvailability> CheckAsync(IReadOnlyCollection<Guid> documentIds, string viewerSub, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerSub);
        if (documentIds.Count == 0)
        {
            return new DocumentAvailability(new HashSet<Guid>(), new HashSet<Guid>(), new Dictionary<Guid, string>());
        }

        var wanted = documentIds.Distinct().ToArray();
        var rows = await db.Documents.AsNoTracking().ReadableBy(viewerSub)
            .Where(d => wanted.Contains(d.Id))
            .Select(d => new { d.Id, d.Title, Searchable = d.Status == DocumentStatus.Active && d.ActiveVersionId != null })
            .ToListAsync(cancellationToken);

        return new DocumentAvailability(
            rows.Select(r => r.Id).ToHashSet(),
            rows.Where(r => r.Searchable).Select(r => r.Id).ToHashSet(),
            rows.ToDictionary(r => r.Id, r => r.Title));
    }
}
