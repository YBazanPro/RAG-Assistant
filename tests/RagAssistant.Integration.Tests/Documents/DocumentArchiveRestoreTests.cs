using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 009, AC-15 to AC-19.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class DocumentArchiveRestoreTests(PostgresFixture postgres)
{
    [Test]
    public async Task Archive_activeDocument_returns200ArchivedAndLeavesVersionsAndPassagesAlone()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.Maintainer);
        var versions = await CountAsync(app, "document_versions", doc.DocumentId);
        var passages = await CountAsync(app, "passages", doc.DocumentId);

        var response = await PostActionAsync(client, doc.DocumentId, "archive");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("archived");
        await Assert.That(json.GetProperty("updatedBy").GetString()).IsEqualTo("test-Maintainer");
        await Assert.That(json.GetProperty("revision").GetInt32()).IsEqualTo(1);
        await Assert.That(await CountAsync(app, "document_versions", doc.DocumentId)).IsEqualTo(versions);
        await Assert.That(await CountAsync(app, "passages", doc.DocumentId)).IsEqualTo(passages);
    }

    [Test]
    public async Task Archive_listExcludesArchivedByDefaultAndStatusFilterIncludesIt()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        var byDefault = await client.GetFromJsonAsync<JsonElement>("/api/documents?pageSize=100");
        var archived = await client.GetFromJsonAsync<JsonElement>("/api/documents?status=archived&pageSize=100");

        await Assert.That(byDefault.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == doc.DocumentId)).IsFalse();
        await Assert.That(archived.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == doc.DocumentId)).IsTrue();
    }

    [Test]
    public async Task Archive_repeated_returns409AlreadyArchived()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        var response = await PostActionAsync(client, doc.DocumentId, "archive");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.already_archived");
    }

    [Test]
    public async Task Archive_andRestore_unknownDocument_return404()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        await Assert.That((await PostActionAsync(client, Guid.NewGuid(), "archive")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await PostActionAsync(client, Guid.NewGuid(), "restore")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Archive_thenImportSameContent_isAccepted()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var markdown = IngestionHarness.UniqueMarkdown();
        var doc = await ImportIndexedAsync(app, markdown);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        using var form = DocumentUploads.Form("again.md", Encoding.UTF8.GetBytes(markdown), DocumentUploads.UniqueTitle());
        var response = await client.PostAsync("/api/documents", form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(await DocumentStatusAsync(app, doc.DocumentId)).IsEqualTo("Archived");
    }

    [Test]
    public async Task Restore_archivedDocument_returns200AndIsActiveAgainWithItsActiveVersion()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        var response = await PostActionAsync(client, doc.DocumentId, "restore");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("active");
        await Assert.That(json.GetProperty("activeVersionId").GetGuid()).IsEqualTo(doc.VersionId);
        await Assert.That(await DocumentStatusAsync(app, doc.DocumentId)).IsEqualTo("Active");
    }

    [Test]
    public async Task Restore_activeDocument_returns409NotArchived()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await PostActionAsync(client, doc.DocumentId, "restore");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.not_archived");
    }

    [Test]
    public async Task Restore_whenAnotherActiveDocumentHasTheSameContent_returns409AndStaysArchived()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var markdown = IngestionHarness.UniqueMarkdown();
        var doc = await ImportIndexedAsync(app, markdown);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();
        var twin = await ImportIndexedAsync(app, markdown);

        var response = await PostActionAsync(client, doc.DocumentId, "restore");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(json.GetProperty("code").GetString()).IsEqualTo("documents.duplicate_content");
        await Assert.That(json.GetProperty("existingDocumentId").GetGuid()).IsEqualTo(twin.DocumentId);
        await Assert.That(await DocumentStatusAsync(app, doc.DocumentId)).IsEqualTo("Archived");
    }

    [Test]
    public async Task Archive_whileARunIsActive_theRunStillIndexesAndTheDocumentStaysArchived()
    {
        await using var app = await StartWithoutEmbeddingsAsync(postgres);
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        app.Generator.Available = true;
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That(await app.ScalarAsync<string>("SELECT status FROM documents.document_versions WHERE id = $1", doc.VersionId)).IsEqualTo("Indexed");
        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId)).IsEqualTo(doc.VersionId);
        await Assert.That(await DocumentStatusAsync(app, doc.DocumentId)).IsEqualTo("Archived");
    }

    [Test]
    public async Task Restore_documentWithFailedLatestVersion_isAllowedAndShowsFailed()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        var response = await PostActionAsync(client, doc.DocumentId, "restore");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(response)).GetProperty("status").GetString()).IsEqualTo("failed");
    }
}
