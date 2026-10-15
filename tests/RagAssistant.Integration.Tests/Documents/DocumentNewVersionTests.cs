using System.Net;
using System.Text;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 009, AC-6 to AC-14.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class DocumentNewVersionTests(PostgresFixture postgres)
{
    private static Task<List<object?[]>> VersionsAsync(IngestionHarness app, Guid documentId) =>
        app.RowsAsync(
            "SELECT number, origin, status, source_format, content_hash FROM documents.document_versions WHERE document_id = $1 ORDER BY number",
            documentId);

    [Test]
    public async Task UploadVersion_toIndexedDocument_returns202AndQueuesVersionTwoWhileVersionOneStaysActive()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        app.Generator.Available = false;
        using var client = app.CreateClientAs(Persona.Maintainer);
        var before = await app.ScalarAsync<DateTime>("SELECT updated_at FROM documents.documents WHERE id = $1", doc.DocumentId);

        var response = await UploadVersionAsync(client, doc.DocumentId, Markdown());
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(json.GetProperty("versionNumber").GetInt32()).IsEqualTo(2);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("queued");
        var runId = json.GetProperty("ingestionRunId").GetGuid();
        var versions = await VersionsAsync(app, doc.DocumentId);
        await Assert.That(versions[1][1]).IsEqualTo("Upload");
        await Assert.That(versions[1][2]).IsEqualTo("Queued");
        await Assert.That(await app.ScalarAsync<int>("SELECT attempt FROM documents.ingestion_runs WHERE id = $1", runId)).IsEqualTo(1);
        await Assert.That((await app.EventsAsync(runId))[0]).IsEqualTo("Queued/Completed");
        await Assert.That(await app.ScalarAsync<string>("SELECT latest_content_hash FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo((string)versions[1][4]!);
        await Assert.That(await app.ScalarAsync<DateTime>("SELECT updated_at FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsGreaterThan(before);
        await Assert.That(await app.ScalarAsync<string>("SELECT updated_by FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo("test-Maintainer");
        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo(doc.VersionId);
    }

    [Test]
    public async Task UploadVersion_whenWorkerRuns_supersedesVersionOneAndKeepsItsPassages()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var json = await JsonAsync(await UploadVersionAsync(client, doc.DocumentId, Markdown()));
        await app.WaitForRunAsync(json.GetProperty("ingestionRunId").GetGuid(), "Succeeded");

        var versions = await VersionsAsync(app, doc.DocumentId);
        await Assert.That(versions[0][2]).IsEqualTo("Superseded");
        await Assert.That(versions[1][2]).IsEqualTo("Indexed");
        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo(json.GetProperty("versionId").GetGuid());
        await Assert.That(await app.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId)).IsEqualTo(2L);
    }

    [Test]
    public async Task UploadVersion_textFileForMarkdownDocument_usesPlainTextFormat()
    {
        await using var app = await StartWithoutEmbeddingsAsync(postgres);
        var doc = await app.ImportAsync();
        await app.ExecuteAsync("UPDATE documents.document_versions SET status = 'Indexed' WHERE id = $1", doc.VersionId);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await UploadVersionAsync(client, doc.DocumentId, Markdown(), "notes.txt");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That((await VersionsAsync(app, doc.DocumentId))[1][3]).IsEqualTo("PlainText");
    }

    [Test]
    public async Task UploadVersion_sameContentAsLatestDifferentLineEndingsAndBom_returns409WithOwnId()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var markdown = IngestionHarness.UniqueMarkdown();
        var doc = await ImportIndexedAsync(app, markdown);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(markdown.Replace("\n", "\r\n"))).ToArray();

        var response = await UploadVersionAsync(client, doc.DocumentId, bytes);
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(json.GetProperty("code").GetString()).IsEqualTo("documents.duplicate_content");
        await Assert.That(json.GetProperty("existingDocumentId").GetGuid()).IsEqualTo(doc.DocumentId);
    }

    [Test]
    public async Task UploadVersion_contentOfAnotherActiveDocument_returns409WithThatId()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var other = await ImportIndexedAsync(app);
        var doc = await ImportIndexedAsync(app);
        var otherContent = await app.ScalarAsync<string>("SELECT content FROM documents.document_versions WHERE id = $1", other.VersionId);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await UploadVersionAsync(client, doc.DocumentId, Encoding.UTF8.GetBytes(otherContent!));
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(json.GetProperty("existingDocumentId").GetGuid()).IsEqualTo(other.DocumentId);
    }

    [Test]
    public async Task UploadVersion_contentOfAnOlderVersion_isAcceptedAsARollback()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var original = IngestionHarness.UniqueMarkdown();
        var doc = await ImportIndexedAsync(app, original);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        var second = await JsonAsync(await UploadVersionAsync(client, doc.DocumentId, Markdown()));
        await app.WaitForRunAsync(second.GetProperty("ingestionRunId").GetGuid(), "Succeeded");

        var response = await UploadVersionAsync(client, doc.DocumentId, Encoding.UTF8.GetBytes(original));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That((await JsonAsync(response)).GetProperty("versionNumber").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task UploadVersion_contentOfAnArchivedDocument_isAccepted()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var archived = await ImportIndexedAsync(app);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, archived.DocumentId, "archive")).EnsureSuccessStatusCode();
        var content = await app.ScalarAsync<string>("SELECT content FROM documents.document_versions WHERE id = $1", archived.VersionId);

        var response = await UploadVersionAsync(client, doc.DocumentId, Encoding.UTF8.GetBytes(content!));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    [Arguments("none", "documents.file_required")]
    [Arguments("pdf", "documents.unsupported_extension")]
    [Arguments("empty", "documents.empty_content")]
    [Arguments("utf8", "documents.invalid_utf8")]
    [Arguments("big", "documents.file_too_large")]
    public async Task UploadVersion_fileRuleViolation_returns400WithImportCodeAndPersistsNothing(string kind, string code)
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        var (bytes, name) = kind switch
        {
            "none" => ((byte[]?)null, "x.md"),
            "pdf" => (Markdown(), "x.pdf"),
            "empty" => ("  \n"u8.ToArray(), "x.md"),
            "utf8" => ([0xFF, 0xFE, 0xFD], "x.md"),
            _ => (new byte[DocumentUploads.DefaultMaxFileSizeBytes + 1], "x.md"),
        };

        var response = await UploadVersionAsync(client, doc.DocumentId, bytes, name);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CodeAsync(response)).IsEqualTo(code);
        await Assert.That(await CountAsync(app, "document_versions", doc.DocumentId)).IsEqualTo(1L);
    }

    [Test]
    public async Task UploadVersion_whileLatestVersionIsQueued_returns409InProgress()
    {
        await using var app = await StartWithoutEmbeddingsAsync(postgres);
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await UploadVersionAsync(client, doc.DocumentId, Markdown());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.ingestion_in_progress");
    }

    [Test]
    public async Task UploadVersion_twoSimultaneousUploads_acceptExactlyOne()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var a = app.CreateClientAs(Persona.ContentOwner);
        using var b = app.CreateClientAs(Persona.Maintainer);

        var responses = await Task.WhenAll(
            UploadVersionAsync(a, doc.DocumentId, Markdown("From A, long enough to index.")),
            UploadVersionAsync(b, doc.DocumentId, Markdown("From B, long enough to index.")));

        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Accepted)).IsEqualTo(1);
        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)).IsEqualTo(1);
        await Assert.That(await CountAsync(app, "document_versions", doc.DocumentId)).IsEqualTo(2L);
    }

    [Test]
    public async Task UploadVersion_toArchivedDocument_returns409Archived()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        var response = await UploadVersionAsync(client, doc.DocumentId, Markdown());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.archived");
    }

    [Test]
    public async Task UploadVersion_unknownDocument_returns404()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await UploadVersionAsync(client, Guid.NewGuid(), Markdown());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UploadVersion_afterAFailedVersion_isAcceptedAndTheFailedOneStaysInHistory()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        app.Generator.AlwaysFail = false;
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await UploadVersionAsync(client, doc.DocumentId, Markdown());
        await app.WaitForRunAsync((await JsonAsync(response)).GetProperty("ingestionRunId").GetGuid(), "Succeeded");

        var versions = await VersionsAsync(app, doc.DocumentId);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(versions[0][2]).IsEqualTo("Failed");
        await Assert.That(versions[1][2]).IsEqualTo("Indexed");
    }

    [Test]
    public async Task UploadVersion_thatFailsIngestion_keepsThePreviousVersionActive()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        var doc = await ImportIndexedAsync(app);
        app.Generator.AlwaysFail = true;
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var json = await JsonAsync(await UploadVersionAsync(client, doc.DocumentId, Markdown()));
        await app.WaitForRunAsync(json.GetProperty("ingestionRunId").GetGuid(), "Failed");

        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo(doc.VersionId);
        await Assert.That((await VersionsAsync(app, doc.DocumentId))[0][2]).IsEqualTo("Indexed");
    }

    [Test]
    public async Task Reprocess_indexedDocument_copiesContentAndKeepsOldVersionActiveUntilIndexed()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        var oldPassages = (await app.RowsAsync("SELECT id FROM documents.passages WHERE version_id = $1", doc.VersionId)).Select(r => (Guid)r[0]!).ToList();
        var hash = await app.ScalarAsync<string>("SELECT latest_content_hash FROM documents.documents WHERE id = $1", doc.DocumentId);
        app.Generator.Available = false;
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await PostActionAsync(client, doc.DocumentId, "reprocess");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var versions = await VersionsAsync(app, doc.DocumentId);
        await Assert.That(versions[1][1]).IsEqualTo("Reprocess");
        await Assert.That(versions[1][2]).IsEqualTo("Queued");
        await Assert.That(versions[1][4]).IsEqualTo(versions[0][4]);
        await Assert.That(versions[1][3]).IsEqualTo(versions[0][3]);
        await Assert.That(await app.ScalarAsync<string>("SELECT latest_content_hash FROM documents.documents WHERE id = $1", doc.DocumentId)).IsEqualTo(hash);
        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo(doc.VersionId);

        app.Generator.Available = true;
        await app.WaitForRunAsync(json.GetProperty("ingestionRunId").GetGuid(), "Succeeded");
        var after = await VersionsAsync(app, doc.DocumentId);
        await Assert.That(after[0][2]).IsEqualTo("Superseded");
        await Assert.That(after[1][2]).IsEqualTo("Indexed");
        var newPassages = (await app.RowsAsync("SELECT id FROM documents.passages WHERE version_id = $1", json.GetProperty("versionId").GetGuid())).Select(r => (Guid)r[0]!);
        await Assert.That(newPassages.Intersect(oldPassages).Any()).IsFalse();
    }

    [Test]
    public async Task Reprocess_failedLatestVersion_returns409NotReprocessable()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await PostActionAsync(client, doc.DocumentId, "reprocess");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.not_reprocessable");
    }

    [Test]
    public async Task Reprocess_queuedLatestVersion_returns409InProgress()
    {
        await using var app = await StartWithoutEmbeddingsAsync(postgres);
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await PostActionAsync(client, doc.DocumentId, "reprocess");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.ingestion_in_progress");
    }

    [Test]
    public async Task Reprocess_archivedDocument_returns409Archived()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        var response = await PostActionAsync(client, doc.DocumentId, "reprocess");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.archived");
    }
}
