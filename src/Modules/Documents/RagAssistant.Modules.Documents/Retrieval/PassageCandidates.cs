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

/// <summary>A keyword match: <c>ts_rank_cd</c> of the passage for the query (spec 019).</summary>
/// <param name="Coverage">The share of the question's distinct terms the passage contains, in (0, 1].</param>
internal sealed record KeywordHit(Guid PassageId, double Score, double Coverage);

/// <summary>The eligible-passage queries of retrieval (spec 011, eligibility and pipeline step 3; spec 019, keyword pool).</summary>
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

        var ranked = Eligible(embeddingModel, filter)
            .OrderBy(x => x.Passage.Embedding!.CosineDistance(vector))
            .Take(poolSize);
        return await ProjectAsync(ranked, vector, cancellationToken);
    }

    /// <summary>
    /// The <paramref name="poolSize"/> eligible passages that best match <paramref name="query"/> as keywords (spec 019), best first:
    /// any of the question's lexemes in the configuration of the query's language, ranked with <c>ts_rank_cd</c>. Same eligibility and
    /// filters as the vector pool. A query without lexemes (only stop-words or punctuation) matches nothing.
    /// </summary>
    public async Task<IReadOnlyList<KeywordHit>> KeywordAsync(
        string query, string? queryLanguage, string embeddingModel, CandidateFilter filter, int poolSize, CancellationToken cancellationToken)
    {
        // The configuration comes from the same SQL function that sets each passage's own (migration HybridSearch).
        // The lexemes are OR-ed: websearch_to_tsquery would AND them, and a passage rarely holds every word of a question. They are
        // quoted into the tsquery as they are (quotes and backslashes doubled), so nothing parses or stems them a second time.
        var language = queryLanguage ?? string.Empty;
        var matches = db.Database.SqlQuery<KeywordRow>($"""
            SELECT p.id AS "PassageId",
                ts_rank_cd(p.search_vector, q.query)::double precision AS "Score",
                (SELECT count(*) FROM unnest(q.lexemes) AS l WHERE l = ANY (tsvector_to_array(p.search_vector)))::double precision
                    / cardinality(q.lexemes) AS "Coverage"
            FROM documents.passages p,
                (SELECT array_agg(l) AS lexemes,
                        string_agg('''' || replace(replace(l, '\', '\\'), '''', '''''') || '''', ' | ')::tsquery AS query
                    FROM unnest(tsvector_to_array(to_tsvector(documents.search_config_for({language}), {query}))) AS l) AS q
            WHERE p.search_vector @@ q.query
            """);

        var rows = await (
                from m in matches
                join e in Eligible(embeddingModel, filter) on m.PassageId equals e.Passage.Id
                orderby m.Score descending, m.PassageId
                select new { m.PassageId, m.Score, m.Coverage })
            .Take(poolSize)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new KeywordHit(r.PassageId, r.Score, r.Coverage))];
    }

    /// <summary>The given eligible passages with their cosine to the query, in no guaranteed order: keyword hits the vector pool missed.</summary>
    public async Task<IReadOnlyList<RetrievedChunk>> FetchByIdsAsync(
        float[] query, string embeddingModel, CandidateFilter filter, IReadOnlyCollection<Guid> passageIds, CancellationToken cancellationToken)
    {
        var vector = new Vector(query);
        var wanted = passageIds.ToArray();
        return await ProjectAsync(Eligible(embeddingModel, filter).Where(x => wanted.Contains(x.Passage.Id)), vector, cancellationToken);
    }

    private static async Task<IReadOnlyList<RetrievedChunk>> ProjectAsync(
        IQueryable<EligibleRow> rows, Vector vector, CancellationToken cancellationToken)
    {
        var projected = await rows
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
            .. projected.Select(r => new RetrievedChunk(
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

    /// <summary>The shape of the raw keyword query; EF composes the eligibility joins on top of it.</summary>
    private sealed class KeywordRow
    {
        public Guid PassageId { get; init; }

        public double Score { get; init; }

        public double Coverage { get; init; }
    }

    // A member-init projection, so EF can keep composing on it (a constructor projection cannot be ordered by).
    private sealed class EligibleRow
    {
        public required Passage Passage { get; init; }

        public required DocumentVersion Version { get; init; }

        public required Document Document { get; init; }
    }
}
