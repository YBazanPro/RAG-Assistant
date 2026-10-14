using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 006, AC-1 to AC-13 and AC-16.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class DocumentImportTests(RagAssistantAppFactory factory)
{
    private const string Route = "/api/documents";

    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private async Task<(HttpResponseMessage Response, JsonElement Body)> ImportAsync(
        MultipartFormDataContent form, Persona persona = Persona.ContentOwner)
    {
        using var client = factory.CreateClientAs(persona);
        using (form)
        {
            var response = await client.PostAsync(Route, form);
            var body = response.Content.Headers.ContentLength is 0
                ? default
                : await response.Content.ReadFromJsonAsync<JsonElement>();
            return (response, body);
        }
    }

    private async Task<Guid> ImportValidAsync(string? title = null, byte[]? bytes = null, string fileName = "policy.md")
    {
        var (response, body) = await ImportAsync(DocumentUploads.Form(fileName, bytes ?? DocumentUploads.UniqueMarkdown(), title ?? DocumentUploads.UniqueTitle()));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        return body.GetProperty("documentId").GetGuid();
    }

    private async Task<string?> VersionColumnAsync(Guid documentId, string column) =>
        await DocumentUploads.ScalarAsync<string>(
            Db, $"SELECT {column}::text FROM documents.document_versions WHERE document_id = $1", documentId);

    [Test]
    public async Task Import_validMarkdown_returns202WithLocationAndBody()
    {
        var (response, body) = await ImportAsync(DocumentUploads.Form("budget.md", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle()));

        var documentId = body.GetProperty("documentId").GetGuid();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo($"{Route}/{documentId}");
        await Assert.That(body.GetProperty("versionNumber").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("queued");
        await Assert.That(body.GetProperty("versionId").GetGuid()).IsNotEqualTo(Guid.Empty);
        await Assert.That(body.GetProperty("ingestionRunId").GetGuid()).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task Import_validMarkdown_persistsDocumentVersionRunAndEvent()
    {
        var (_, body) = await ImportAsync(DocumentUploads.Form("budget.md", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle()));
        var documentId = body.GetProperty("documentId").GetGuid();
        var versionId = body.GetProperty("versionId").GetGuid();
        var runId = body.GetProperty("ingestionRunId").GetGuid();

        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db, "SELECT status FROM documents.documents WHERE id = $1", documentId)).IsEqualTo("Active");
        await Assert.That(await DocumentUploads.ScalarAsync<Guid?>(
            Db, "SELECT active_version_id FROM documents.documents WHERE id = $1", documentId)).IsNull();
        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db, "SELECT created_by FROM documents.documents WHERE id = $1", documentId)).IsEqualTo("test-ContentOwner");

        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db, "SELECT status || '/' || origin || '/' || source_format || '/' || number FROM documents.document_versions WHERE id = $1", versionId))
            .IsEqualTo("Queued/Upload/Markdown/1");

        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db, "SELECT status || '/' || current_stage || '/' || attempt FROM documents.ingestion_runs WHERE id = $1", runId))
            .IsEqualTo("Queued/Queued/1");

        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db, "SELECT stage || '/' || status FROM documents.ingestion_events WHERE run_id = $1", runId))
            .IsEqualTo("Queued/Completed");
        await Assert.That(await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM documents.ingestion_events WHERE run_id = $1", runId)).IsEqualTo(1L);
    }

    [Test]
    [Arguments("notes.txt", "PlainText")]
    [Arguments("notes.TXT", "PlainText")]
    [Arguments("notes.markdown", "Markdown")]
    [Arguments("NOTES.MARKDOWN", "Markdown")]
    public async Task Import_extension_setsSourceFormat(string fileName, string expectedFormat)
    {
        var documentId = await ImportValidAsync(fileName: fileName);

        await Assert.That(await VersionColumnAsync(documentId, "source_format")).IsEqualTo(expectedFormat);
    }

    [Test]
    public async Task Import_bomAndCrlf_storesNormalizedContentAndHash()
    {
        var marker = Guid.NewGuid();
        var raw = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes($"# Title {marker}\r\nline two\r\n")).ToArray();

        var documentId = await ImportValidAsync(bytes: raw);

        var stored = await VersionColumnAsync(documentId, "content");
        await Assert.That(stored).IsEqualTo($"# Title {marker}\nline two\n");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stored!)));
        await Assert.That(await VersionColumnAsync(documentId, "content_hash")).IsEqualTo(expectedHash);
        await Assert.That(await VersionColumnAsync(documentId, "size_bytes")).IsEqualTo(raw.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Import_optionalMetadataOmitted_usesDefaults()
    {
        var documentId = await ImportValidAsync();

        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);
        var detail = await client.GetFromJsonAsync<JsonElement>($"{Route}/{documentId}");

        await Assert.That(detail.GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(detail.GetProperty("tags").GetArrayLength()).IsEqualTo(0);
        await Assert.That(detail.GetProperty("effectiveDate").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(detail.GetProperty("source").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Import_tagsAndLanguage_areNormalized()
    {
        var form = DocumentUploads.Form(
            "a.md", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle(), "Finance",
            ("language", "EN"), ("tags", " Budget "), ("tags", "budget"), ("tags", "FINANCE"),
            ("effectiveDate", "2026-01-31"), ("source", "  HR handbook 2026  "));

        var (response, body) = await ImportAsync(form);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);

        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);
        var detail = await client.GetFromJsonAsync<JsonElement>($"{Route}/{body.GetProperty("documentId").GetGuid()}");

        await Assert.That(detail.GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(string.Join(",", detail.GetProperty("tags").EnumerateArray().Select(t => t.GetString()))).IsEqualTo("budget,finance");
        await Assert.That(detail.GetProperty("effectiveDate").GetString()).IsEqualTo("2026-01-31");
        await Assert.That(detail.GetProperty("source").GetString()).IsEqualTo("HR handbook 2026");
    }

    [Test]
    public async Task Import_fileNameWithPath_storesLastSegment()
    {
        var documentId = await ImportValidAsync(fileName: "../../etc/policy.md");

        await Assert.That(await VersionColumnAsync(documentId, "original_file_name")).IsEqualTo("policy.md");
    }

    [Test]
    [Arguments("no-file", "documents.file_required")]
    [Arguments("two-files", "documents.file_required")]
    [Arguments("pdf", "documents.unsupported_extension")]
    [Arguments("no-extension", "documents.unsupported_extension")]
    [Arguments("empty", "documents.empty_content")]
    [Arguments("whitespace", "documents.empty_content")]
    [Arguments("bom-only", "documents.empty_content")]
    [Arguments("invalid-utf8", "documents.invalid_utf8")]
    [Arguments("too-large", "documents.file_too_large")]
    public async Task Import_badFile_returns400WithCodeAndPersistsNothing(string scenario, string expectedCode)
    {
        var title = DocumentUploads.UniqueTitle();
        var form = scenario switch
        {
            "no-file" => DocumentUploads.Form("x.md", null, title),
            "two-files" => TwoFiles(title),
            "pdf" => DocumentUploads.Form("a.pdf", DocumentUploads.UniqueMarkdown(), title),
            "no-extension" => DocumentUploads.Form("README", DocumentUploads.UniqueMarkdown(), title),
            "empty" => DocumentUploads.Form("a.md", [], title),
            "whitespace" => DocumentUploads.Form("a.md", "  \r\n\t \n"u8.ToArray(), title),
            "bom-only" => DocumentUploads.Form("a.md", Encoding.UTF8.GetPreamble(), title),
            "invalid-utf8" => DocumentUploads.Form("a.md", [0x23, 0x20, 0xE9, 0x20, 0xFF], title),
            "too-large" => DocumentUploads.Form("a.md", new byte[DocumentUploads.DefaultMaxFileSizeBytes + 1], title),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var (response, body) = await ImportAsync(form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo(expectedCode);
        await Assert.That(body.GetProperty("errors").TryGetProperty("file", out _)).IsTrue();
        await Assert.That(await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM documents.documents WHERE title = $1", title)).IsEqualTo(0L);
    }

    private static MultipartFormDataContent TwoFiles(string title)
    {
        var form = DocumentUploads.Form("a.md", DocumentUploads.UniqueMarkdown(), title);
        form.Add(new ByteArrayContent(DocumentUploads.UniqueMarkdown()), "file", "b.md");
        return form;
    }

    [Test]
    public async Task Import_notMultipart_returns400WithMetadataErrors()
    {
        // A JSON body carries no form fields, so metadata validation (which runs before the file rules) rejects it.
        using var client = factory.CreateClientAs(Persona.ContentOwner);

        var response = await client.PostAsJsonAsync(Route, new { title = "x", ownerName = "y" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(body.GetProperty("errors").TryGetProperty("title", out _)).IsTrue();
        await Assert.That(body.GetProperty("errors").TryGetProperty("ownerName", out _)).IsTrue();
    }

    [Test]
    [Arguments("missing-title", "title")]
    [Arguments("long-title", "title")]
    [Arguments("missing-owner", "ownerName")]
    [Arguments("long-owner", "ownerName")]
    [Arguments("bad-language", "language")]
    [Arguments("21-tags", "tags")]
    [Arguments("long-tag", "tags")]
    [Arguments("bad-date", "effectiveDate")]
    [Arguments("long-source", "source")]
    public async Task Import_invalidMetadata_returns400WithFieldError(string scenario, string field)
    {
        var bytes = DocumentUploads.UniqueMarkdown();
        var form = scenario switch
        {
            "missing-title" => DocumentUploads.Form("a.md", bytes, title: null),
            "long-title" => DocumentUploads.Form("a.md", bytes, new string('t', 201)),
            "missing-owner" => DocumentUploads.Form("a.md", bytes, "T", ownerName: null),
            "long-owner" => DocumentUploads.Form("a.md", bytes, "T", new string('o', 101)),
            "bad-language" => DocumentUploads.Form("a.md", bytes, "T", "O", ("language", "english")),
            "21-tags" => DocumentUploads.Form("a.md", bytes, "T", "O", [.. Enumerable.Range(0, 21).Select(i => ("tags", $"tag{i}"))]),
            "long-tag" => DocumentUploads.Form("a.md", bytes, "T", "O", ("tags", new string('x', 51))),
            "bad-date" => DocumentUploads.Form("a.md", bytes, "T", "O", ("effectiveDate", "31/01/2026")),
            "long-source" => DocumentUploads.Form("a.md", bytes, "T", "O", ("source", new string('s', 501))),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var (response, body) = await ImportAsync(form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(body.GetProperty("errors").TryGetProperty(field, out _)).IsTrue();
    }

    [Test]
    public async Task Import_fileAtExactSizeLimit_returns202()
    {
        var prefix = Encoding.UTF8.GetBytes($"# {Guid.NewGuid()}\n");
        var bytes = new byte[DocumentUploads.DefaultMaxFileSizeBytes];
        prefix.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)'a', prefix.Length, bytes.Length - prefix.Length);

        var (response, _) = await ImportAsync(DocumentUploads.Form("big.md", bytes, DocumentUploads.UniqueTitle()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task Import_duplicateOfActiveDocument_returns409WithExistingId()
    {
        var marker = Guid.NewGuid();
        var firstId = await ImportValidAsync(bytes: Encoding.UTF8.GetBytes($"# Dup {marker}\nbody\n"));
        var variant = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes($"# Dup {marker}\r\nbody\r\n")).ToArray();
        var title = DocumentUploads.UniqueTitle();

        var (response, body) = await ImportAsync(DocumentUploads.Form("copy.md", variant, title));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.duplicate_content");
        await Assert.That(body.GetProperty("existingDocumentId").GetGuid()).IsEqualTo(firstId);
        await Assert.That(await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM documents.documents WHERE title = $1", title)).IsEqualTo(0L);
    }

    [Test]
    public async Task Import_concurrentIdenticalContent_exactlyOneIsAccepted()
    {
        var bytes = DocumentUploads.UniqueMarkdown();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => ImportAsync(DocumentUploads.Form("same.md", bytes, DocumentUploads.UniqueTitle()))));

        var statuses = attempts.Select(a => a.Response.StatusCode).ToArray();
        await Assert.That(statuses.Count(s => s == HttpStatusCode.Accepted)).IsEqualTo(1);
        await Assert.That(statuses.Count(s => s == HttpStatusCode.Conflict)).IsEqualTo(5);
        var accepted = attempts.Single(a => a.Response.StatusCode == HttpStatusCode.Accepted).Body.GetProperty("documentId").GetGuid();
        await Assert.That(attempts.Where(a => a.Response.StatusCode == HttpStatusCode.Conflict)
            .All(a => a.Body.GetProperty("existingDocumentId").GetGuid() == accepted)).IsTrue();
    }

    [Test]
    public async Task Import_sameTitleDifferentContent_returns202()
    {
        var title = DocumentUploads.UniqueTitle();

        await ImportValidAsync(title);
        await ImportValidAsync(title);

        await Assert.That(await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM documents.documents WHERE title = $1", title)).IsEqualTo(2L);
    }

    [Test]
    public async Task Import_asAnonymous_returns401()
    {
        var (response, _) = await ImportAsync(DocumentUploads.Form("a.md", DocumentUploads.UniqueMarkdown()), Persona.Anonymous);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Import_asKnowledgeSeeker_returns403()
    {
        var (response, _) = await ImportAsync(DocumentUploads.Form("a.md", DocumentUploads.UniqueMarkdown()), Persona.KnowledgeSeeker);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Import_asMaintainer_returns202()
    {
        var (response, _) = await ImportAsync(DocumentUploads.Form("a.md", DocumentUploads.UniqueMarkdown()), Persona.Maintainer);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }
}
