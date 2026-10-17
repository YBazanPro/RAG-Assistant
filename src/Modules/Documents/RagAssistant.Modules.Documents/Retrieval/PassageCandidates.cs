using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <param name="Language">Already lowercased, or null.</param>
/// <param name="Tags">Already normalized like import.</param>
/// <param name="DocumentIds">Null for no restriction; an empty list matches no document (spec 015).</param>
internal sealed record CandidateFilter(string? Language, IReadOnlyList<string> Tags, IReadOnlyList<Guid>? DocumentIds = null)
{
    /// <summary>Whose documents may be read (spec 016); set by the search handler from the caller, never from a request filter.</summary>
    public required string ViewerSub { get; init; }
}

/// <summary>The eligible-passage queries of retrieval (spec 011, eligibility and pipeline step 3).</summary>
internal sealed class PassageCandidates(DocumentsDbContext db)
{
    private const int DefaultEfSearch = 40;

    /// <summary>The <paramref name="poolSize"/> nearest eligible passages, scored as 1 − cosine distance, in no guaranteed order.</summary>
    public async Task<IReadOnlyList<RetrievedChunk>> FetchAsync(
        float[] query, string embeddingModel, CandidateFilter filter, int poolSize, CancellationToken cancellationToken)
    {
        var vector = new Vector(query);

        // SET LOCAL lives only inside a transaction. The iterative scan keeps a filtered HNSW scan going until the pool fills,
        // and ef_search lets one scan pass return the whole pool (pgvector 0.8).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var efSearch = Math.Max(DefaultEfSearch, poolSize).ToString(CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('hnsw.iterative_scan', 'relaxed_order', true), set_config('hnsw.ef_search', {efSearch}, true)",
            cancellationToken);

        var rows = await Eligible(embeddingModel, filter)
            .OrderBy(x => x.Passage.Embedding!.CosineDistance(vector))
            .Take(poolSize)
            .Select(x => new
            {
                PassageId = x.Passage.Id,
                DocumentId = x.Document.Id,
                x.Document.Title,
                x.Document.Language,
                VersionId = x.Version.Id,
                x.Version.Number,
                x.Passage.SectionPath,
                x.Passage.Position,
                x.Passage.PageStart,
                x.Passage.PageEnd,
                x.Passage.Text,
                Distance = x.Passage.Embedding!.CosineDistance(vector),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(r => new RetrievedChunk(
                r.PassageId, r.DocumentId, r.Title, r.Language, r.VersionId, r.Number, r.SectionPath, r.Position,
                r.PageStart, r.PageEnd, r.Text, 1 - r.Distance)),
        ];
    }

    /// <summary>Active documents matching the filter whose active version was embedded by another model; they need reprocessing.</summary>
    public Task<int> CountModelMismatchesAsync(string embeddingModel, CandidateFilter filter, CancellationToken cancellationToken) =>
        Filtered(db.Documents.AsNoTracking().Where(d => d.Status == DocumentStatus.Active), filter)
            .Join(db.Versions, d => d.ActiveVersionId, v => (Guid?)v.Id, (d, v) => v)
            .CountAsync(v => v.EmbeddingModel != embeddingModel, cancellationToken);

    private IQueryable<EligibleRow> Eligible(string embeddingModel, CandidateFilter filter) =>
        from d in Filtered(db.Documents.AsNoTracking(), filter)
        join v in db.Versions.AsNoTracking() on d.ActiveVersionId equals (Guid?)v.Id
        join p in db.Passages.AsNoTracking() on v.Id equals p.VersionId
        where d.Status == DocumentStatus.Active && v.EmbeddingModel == embeddingModel && p.Embedding != null
        select new EligibleRow { Passage = p, Version = v, Document = d };

    private static IQueryable<Document> Filtered(IQueryable<Document> documents, CandidateFilter filter)
    {
        documents = documents.ReadableBy(filter.ViewerSub);

        if (filter.Language is { } language)
        {
            documents = documents.Where(d => d.Language == language);
        }

        if (filter.DocumentIds is { } ids)
        {
            var wanted = ids.ToArray();
            documents = documents.Where(d => wanted.Contains(d.Id));
        }

        foreach (var tag in filter.Tags)
        {
            documents = documents.Where(d => d.Tags.Contains(tag));
        }

        return documents;
    }

    // A member-init projection, so EF can keep composing on it (a constructor projection cannot be ordered by).
    private sealed class EligibleRow
    {
        public required Passage Passage { get; init; }

        public required DocumentVersion Version { get; init; }

        public required Document Document { get; init; }
    }
}
