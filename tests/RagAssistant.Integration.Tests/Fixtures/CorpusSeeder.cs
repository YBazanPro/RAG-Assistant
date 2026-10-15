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
    int Passages = 0);

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
                (id, title, owner_name, language, tags, effective_date, status, latest_content_hash, created_at, created_by, updated_at, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'seed', $9, 'seed')
            """,
            documentId, seed.Title, seed.Owner, seed.Language, seed.Tags ?? [], seed.EffectiveDate, seed.Archived ? "Archived" : "Active",
            Guid.NewGuid().ToString("N"), updatedAt);

        var versionId = await AddVersionAsync(
            documentId, 1, seed.VersionStatus, makeActive: seed.VersionStatus == "Indexed", updatedAt, seed.IndexedAt ?? updatedAt, seed.Passages);
        return new SeededDocument(documentId, versionId);
    }

    public async Task<Guid> AddVersionAsync(
        Guid documentId, int number, string status, bool makeActive, DateTimeOffset createdAt, DateTimeOffset? indexedAt = null, int passages = 0)
    {
        var versionId = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO documents.document_versions
                (id, document_id, number, origin, status, source_format, original_file_name, content, content_hash, size_bytes, passage_count, created_at, created_by, indexed_at)
            VALUES ($1, $2, $3, 'Upload', $4, 'Markdown', 'seed.md', 'seed', $5, 4, $6, $7, 'seed', $8)
            """,
            versionId, documentId, number, status, Guid.NewGuid().ToString("N"),
            status is "Indexed" or "Superseded" ? (object)passages : DBNull.Value, createdAt,
            status is "Indexed" or "Superseded" ? (object)(indexedAt ?? createdAt) : DBNull.Value);

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
