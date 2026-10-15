using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 010, AC-8: the schema changes of the PDF migration.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class PdfSchemaTests(RagAssistantAppFactory factory)
{
    private const string SourceCheck = "ck_document_versions_source";

    private static readonly int[] MissingPages = [31, 32, 40];

    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [Test]
    public async Task Migration_applied_makesContentNullableAndAddsTheNewColumns()
    {
        var columns = await DocumentUploads.ScalarAsync<string>(
            Db,
            "SELECT string_agg(table_name || '.' || column_name || ':' || is_nullable || ':' || data_type, ',' ORDER BY table_name, column_name) " +
            "FROM information_schema.columns WHERE table_schema = 'documents' " +
            "AND ((table_name = 'document_versions' AND column_name IN ('content', 'storage_key', 'pages_without_text')) " +
            "OR (table_name = 'ingestion_events' AND column_name IN ('current', 'total')))");

        await Assert.That(columns).IsEqualTo(
            "document_versions.content:YES:text,document_versions.pages_without_text:YES:ARRAY,document_versions.storage_key:YES:character varying," +
            "ingestion_events.current:YES:integer,ingestion_events.total:YES:integer");
    }

    [Test]
    public async Task StorageKey_isLimitedTo300Characters()
    {
        var length = await DocumentUploads.ScalarAsync<int>(
            Db,
            "SELECT character_maximum_length FROM information_schema.columns " +
            "WHERE table_schema = 'documents' AND table_name = 'document_versions' AND column_name = 'storage_key'");

        await Assert.That(length).IsEqualTo(300);
    }

    [Test]
    public async Task ExistingTextVersions_afterMigration_keepContentAndHaveNoStorageKey()
    {
        var documentId = await ImportMarkdownAsync();

        var row = await DocumentUploads.ScalarAsync<string>(
            Db,
            "SELECT (content IS NOT NULL)::text || ',' || (storage_key IS NULL)::text || ',' || (pages_without_text IS NULL)::text " +
            "FROM documents.document_versions WHERE document_id = $1 AND number = 1",
            documentId);

        await Assert.That(row).IsEqualTo("true,true,true");
    }

    [Test]
    public async Task CheckConstraint_validTextAndPdfVersions_areAccepted()
    {
        var documentId = await ImportMarkdownAsync();

        await InsertVersionAsync(documentId, 2, "Markdown", content: "# text", storageKey: null);
        await InsertVersionAsync(documentId, 3, "Pdf", content: null, storageKey: $"pdf/{Guid.NewGuid():N}.pdf");

        var count = await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM documents.document_versions WHERE document_id = $1", documentId);
        await Assert.That(count).IsEqualTo(3L);
    }

    [Test]
    [Arguments("Pdf", "text", "pdf/a.pdf", 2)] // a PDF with text content
    [Arguments("Pdf", null, null, 3)] // a PDF with neither
    [Arguments("Pdf", "text", null, 4)] // a PDF with content and no key
    [Arguments("Markdown", null, null, 5)] // a text version with neither
    [Arguments("PlainText", null, "pdf/a.pdf", 6)] // a text version with only a key
    [Arguments("Markdown", "text", "pdf/a.pdf", 7)] // a text version with both
    public async Task CheckConstraint_invalidCombination_isRejected(string format, string? content, string? storageKey, int number)
    {
        var documentId = await ImportMarkdownAsync();

        var ex = await Assert.That(() => InsertVersionAsync(documentId, number, format, content, storageKey)).Throws<PostgresException>();

        await Assert.That(ex!.ConstraintName).IsEqualTo(SourceCheck);
        await Assert.That(ex.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
    }

    [Test]
    public async Task PagesWithoutText_storedOnAPdfVersion_roundTripsAsAnIntegerArray()
    {
        var documentId = await ImportMarkdownAsync();
        await InsertVersionAsync(documentId, 2, "Pdf", null, $"pdf/{Guid.NewGuid():N}.pdf", pagesWithoutText: MissingPages);

        var pages = await DocumentUploads.ScalarAsync<int[]>(
            Db, "SELECT pages_without_text FROM documents.document_versions WHERE document_id = $1 AND number = 2", documentId);

        await Assert.That(pages).IsEquivalentTo(MissingPages);
    }

    private async Task<Guid> ImportMarkdownAsync()
    {
        using var client = factory.CreateClientAs(Persona.ContentOwner);
        using var form = DocumentUploads.Form("schema.md", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle());
        using var response = await client.PostAsync("/api/documents", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("documentId").GetGuid();
    }

    private async Task InsertVersionAsync(
        Guid documentId, int number, string format, string? content, string? storageKey, int[]? pagesWithoutText = null)
    {
        await using var command = Db.CreateCommand(
            "INSERT INTO documents.document_versions " +
            "(id, document_id, number, origin, status, source_format, original_file_name, content, storage_key, pages_without_text, " +
            "content_hash, size_bytes, created_at, created_by) " +
            "VALUES ($1, $2, $3, 'Upload', 'Failed', $4, 'file', $5, $6, $7, $8, 1, now(), 'test')");
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = documentId });
        command.Parameters.Add(new NpgsqlParameter { Value = number });
        command.Parameters.Add(new NpgsqlParameter { Value = format });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)content ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)storageKey ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)pagesWithoutText ?? DBNull.Value,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid().ToString("N") });
        await command.ExecuteNonQueryAsync();
    }
}
