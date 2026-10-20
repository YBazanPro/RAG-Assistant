using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <summary>
/// Spec 020: how each cited source compares with the document today, in one query per call. Passage ids are never reused across
/// versions (ADR 0004), so "the same passage" in a newer version means a passage of the active version with the same text hash.
/// </summary>
internal sealed class DocumentSources(DocumentsDbContext db) : IDocumentSources
{
    public async Task<IReadOnlyDictionary<CitedPassage, SourceState>> GetStatesAsync(
        IReadOnlyCollection<CitedPassage> passages, string viewerSub, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerSub);
        if (passages.Count == 0)
        {
            return new Dictionary<CitedPassage, SourceState>();
        }

        var documentIds = passages.Select(p => p.DocumentId).Distinct().ToArray();
        string[] hashes = [.. passages.Select(p => p.TextHash).OfType<string>().Distinct(StringComparer.Ordinal)];
        // Not ReadableBy: a document the viewer lost access to must still come back, as "unavailable" rather than as nothing.
        var documents = await db.Documents.AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new
            {
                d.Id,
                Readable = d.Visibility == Visibility.Shared || d.CreatedBy == viewerSub,
                d.Status,
                d.ActiveVersionId,
                Matching = db.Passages
                    .Where(p => p.VersionId == d.ActiveVersionId && hashes.Contains(p.TextHash))
                    .Select(p => p.TextHash)
                    .Distinct()
                    .ToList(),
            })
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var states = new Dictionary<CitedPassage, SourceState>();
        foreach (var passage in passages.Distinct())
        {
            states[passage] = documents.GetValueOrDefault(passage.DocumentId) switch
            {
                null or { Readable: false } => SourceState.Unavailable,
                { Status: DocumentStatus.Archived } => SourceState.Archived,
                // No indexed version yet, or still the cited one: only an indexed newer version can make a citation "newer".
                { ActiveVersionId: null } => SourceState.Current,
                { ActiveVersionId: var active } when active == passage.VersionId => SourceState.Current,
                var d when passage.TextHash is { } hash && d.Matching.Contains(hash) => SourceState.NewerUnchanged,
                _ => SourceState.NewerChanged,
            };
        }

        return states;
    }
}
