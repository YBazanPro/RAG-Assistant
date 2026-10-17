using System.Globalization;
using Npgsql;

namespace RagAssistant.Integration.Tests.Fixtures;

public sealed record SeedDocument(
    string Title,
    string Owner,
    string Language = "en",
    string[]? Tags = null,
    DateOnly? EffectiveDate = null,
    bool Archived = false,
    string VersionStatus = "Indexed",
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? IndexedAt = null,
    int Passages = 0,
    string? EmbeddingModel = null,
    string Visibility = "Shared",
    string CreatedBy = "seed");

/// <summary>A passage with its embedding, for retrieval tests (spec 011).</summary>
public sealed record SeedPassage(string Text, float[] Embedding, int? PageStart = null, int? PageEnd = null, string[]? SectionPath = null);

public sealed record SeededDocument(Guid DocumentId, Guid VersionId);

/// <summary>
/// Writes corpus rows straight through SQL: the module's DbContext is internal, and the read endpoints must be tested
/// against states (failed, superseded, archived) that the import flow cannot reach in a test.
/// </summary>
public sealed class CorpusSeeder(NpgsqlDataSource db)
{
    public async Task<SeededDocument> AddDocumentAsync(SeedDocument seed)
    {
        var documentId = Guid.CreateVersion7();
        var updatedAt = seed.UpdatedAt ?? DateTimeOffset.UtcNow;
        await ExecuteAsync(
            """
            INSERT INTO documents.documents
                (id, title, owner_name, language, tags, effective_date, status, latest_content_hash, created_at, created_by, updated_at, updated_by, visibility)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $9, $10, $11)
            """,
            documentId, seed.Title, seed.Owner, seed.Language, seed.Tags ?? [], seed.EffectiveDate, seed.Archived ? "Archived" : "Active",
            Guid.NewGuid().ToString("N"), updatedAt, seed.CreatedBy, seed.Visibility);

        var versionId = await AddVersionAsync(
            documentId, 1, seed.VersionStatus, makeActive: seed.VersionStatus == "Indexed", updatedAt, seed.IndexedAt ?? updatedAt, seed.Passages,
            seed.EmbeddingModel);
        return new SeededDocument(documentId, versionId);
    }

    public async Task<Guid> AddVersionAsync(
        Guid documentId, int number, string status, bool makeActive, DateTimeOffset createdAt, DateTimeOffset? indexedAt = null, int passages = 0,
        string? embeddingModel = null)
    {
        var versionId = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO documents.document_versions
                (id, document_id, number, origin, status, source_format, original_file_name, content, content_hash, size_bytes, passage_count, created_at, created_by, indexed_at, embedding_model)
            VALUES ($1, $2, $3, 'Upload', $4, 'Markdown', 'seed.md', 'seed', $5, 4, $6, $7, 'seed', $8, $9)
            """,
            versionId, documentId, number, status, Guid.NewGuid().ToString("N"),
            status is "Indexed" or "Superseded" ? (object)passages : DBNull.Value, createdAt,
            status is "Indexed" or "Superseded" ? (object)(indexedAt ?? createdAt) : DBNull.Value,
            (object?)embeddingModel ?? DBNull.Value);

        if (makeActive)
        {
            await ExecuteAsync("UPDATE documents.documents SET active_version_id = $1 WHERE id = $2", versionId, documentId);
        }

        for (var position = 0; position < passages; position++)
        {
            await ExecuteAsync(
                """
                INSERT INTO documents.passages (id, version_id, position, section_path, section_ordinal, text, char_count, token_estimate)
                VALUES ($1, $2, $3, $4, 0, $5, 10, 3)
                """,
                Guid.CreateVersion7(), versionId, position, new[] { "Section", $"Part {position}" }, $"Passage {position} <script>");
        }

        return versionId;
    }

    /// <summary>Adds embedded passages after any existing ones; the embedding is written as a vector literal, so the data source needs no plugin.</summary>
    public async Task<Guid[]> AddEmbeddedPassagesAsync(Guid versionId, params SeedPassage[] passages)
    {
        await using var next = db.CreateCommand("SELECT coalesce(max(position) + 1, 0) FROM documents.passages WHERE version_id = $1");
        next.Parameters.Add(new NpgsqlParameter { Value = versionId });
        var position = (int)(await next.ExecuteScalarAsync())!;

        var ids = new Guid[passages.Length];
        for (var i = 0; i < passages.Length; i++)
        {
            var passage = passages[i];
            ids[i] = Guid.CreateVersion7();
            var literal = "[" + string.Join(',', passage.Embedding.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "]";
            await ExecuteAsync(
                """
                INSERT INTO documents.passages
                    (id, version_id, position, section_path, section_ordinal, text, char_count, token_estimate, page_start, page_end, embedding)
                VALUES ($1, $2, $3, $4, 0, $5, $6, $7, $8, $9, $10::vector)
                """,
                ids[i], versionId, position + i, passage.SectionPath ?? ["Section"], passage.Text, passage.Text.Length, (passage.Text.Length + 3) / 4,
                passage.PageStart, passage.PageEnd, literal);
        }

        return ids;
    }

    public Task AddRunAsync(Guid versionId, int attempt, string status, string stage, string? errorCode = null, string? errorMessage = null) =>
        ExecuteAsync(
            """
            INSERT INTO documents.ingestion_runs (id, version_id, attempt, status, current_stage, queued_at, error_code, error_message)
            VALUES ($1, $2, $3, $4, $5, now(), $6, $7)
            """,
            Guid.CreateVersion7(), versionId, attempt, status, stage, (object?)errorCode ?? DBNull.Value, (object?)errorMessage ?? DBNull.Value);

    public async Task<Guid> PassageIdAsync(Guid versionId, int position)
    {
        await using var command = db.CreateCommand("SELECT id FROM documents.passages WHERE version_id = $1 AND position = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = versionId });
        command.Parameters.Add(new NpgsqlParameter { Value = position });
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, params object?[] parameters)
    {
        await using var command = db.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync();
    }
}
