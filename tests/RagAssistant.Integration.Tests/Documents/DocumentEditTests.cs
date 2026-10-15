using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 009, AC-1 to AC-5.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class DocumentEditTests(PostgresFixture postgres)
{
    [Test]
    public async Task Put_validBodyWithCurrentETag_updatesMetadataAndBumpsRevision()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var owner = app.CreateClientAs(Persona.ContentOwner);
        using var maintainer = app.CreateClientAs(Persona.Maintainer);
        var before = await owner.GetFromJsonAsync<JsonElement>($"/api/documents/{doc.DocumentId}");
        var versions = await CountAsync(app, "document_versions", doc.DocumentId);
        var passages = await CountAsync(app, "passages", doc.DocumentId);

        var body = new
        {
            title = "  Budget Exception Policy ",
            ownerName = "Legal",
            language = "EN",
            tags = new[] { " Budget", "FINANCE", "budget" },
            effectiveDate = "2026-01-01",
            source = "intranet",
        };
        var response = await PutAsync(maintainer, doc.DocumentId, body, "\"1\"");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.ETag?.Tag).IsEqualTo("\"2\"");
        await Assert.That(json.GetProperty("revision").GetInt32()).IsEqualTo(2);
        await Assert.That(json.GetProperty("title").GetString()).IsEqualTo("Budget Exception Policy");
        await Assert.That(json.GetProperty("ownerName").GetString()).IsEqualTo("Legal");
        await Assert.That(json.GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(json.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray()).IsEquivalentTo(["budget", "finance"]);
        await Assert.That(json.GetProperty("effectiveDate").GetString()).IsEqualTo("2026-01-01");
        await Assert.That(json.GetProperty("source").GetString()).IsEqualTo("intranet");
        await Assert.That(json.GetProperty("updatedBy").GetString()).IsEqualTo("test-Maintainer");
        await Assert.That(json.GetProperty("updatedAt").GetDateTimeOffset()).IsGreaterThan(before.GetProperty("updatedAt").GetDateTimeOffset());
        await Assert.That(json.GetProperty("createdBy").GetString()).IsEqualTo("test-ContentOwner");
        await Assert.That(json.GetProperty("createdAt").GetDateTimeOffset()).IsEqualTo(before.GetProperty("createdAt").GetDateTimeOffset());
        await Assert.That(await CountAsync(app, "document_versions", doc.DocumentId)).IsEqualTo(versions);
        await Assert.That(await CountAsync(app, "passages", doc.DocumentId)).IsEqualTo(passages);
    }

    [Test]
    public async Task Get_returnsETagMatchingRevision()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync($"/api/documents/{doc.DocumentId}");
        var json = await JsonAsync(response);

        await Assert.That(response.Headers.ETag?.Tag).IsEqualTo("\"1\"");
        await Assert.That(json.GetProperty("revision").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Put_invalidFields_returns400WithAnErrorPerFieldAndChangesNothing()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        var body = new
        {
            title = new string('t', 201),
            ownerName = "",
            language = "english",
            tags = Enumerable.Range(0, 21).Select(i => $"tag{i}").ToArray(),
            effectiveDate = "01/02/2026",
            source = new string('s', 501),
        };

        var response = await PutAsync(client, doc.DocumentId, body, "\"1\"");
        var json = await JsonAsync(response);
        var fields = json.GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToArray();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(json.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(fields).IsEquivalentTo(["title", "ownername", "language", "tags", "effectivedate", "source"]);
        await Assert.That(await app.ScalarAsync<int>("SELECT revision FROM documents.documents WHERE id = $1", doc.DocumentId)).IsEqualTo(1);
    }

    [Test]
    public async Task Put_emptyTitleAndOversizedTag_areFieldErrors()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var empty = await PutAsync(client, doc.DocumentId, new { title = "  ", ownerName = "Finance", language = "en", tags = Array.Empty<string>() }, "\"1\"");
        var tag = await PutAsync(client, doc.DocumentId, new { title = "T", ownerName = "Finance", language = "en", tags = new[] { new string('x', 51) } }, "\"1\"");

        await Assert.That((await JsonAsync(empty)).GetProperty("errors").TryGetProperty("title", out _)).IsTrue();
        await Assert.That((await JsonAsync(tag)).GetProperty("errors").TryGetProperty("tags", out _)).IsTrue();
    }

    [Test]
    public async Task Put_staleETag_returns412WithCurrentRevision()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PutAsync(client, doc.DocumentId, ValidBody("First"), "\"1\"")).EnsureSuccessStatusCode();

        var response = await PutAsync(client, doc.DocumentId, ValidBody("Second"), "\"1\"");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(json.GetProperty("code").GetString()).IsEqualTo("documents.edit_conflict");
        await Assert.That(json.GetProperty("currentRevision").GetInt32()).IsEqualTo(2);
        await Assert.That(await app.ScalarAsync<string>("SELECT title FROM documents.documents WHERE id = $1", doc.DocumentId)).IsEqualTo("First");
    }

    [Test]
    [Arguments(null)]
    [Arguments("*")]
    [Arguments("W/\"1\"")]
    public async Task Put_missingOrUnsupportedIfMatch_returns428(string? ifMatch)
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await PutAsync(client, doc.DocumentId, ValidBody(), ifMatch);

        await Assert.That(response.StatusCode).IsEqualTo((HttpStatusCode)428);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.precondition_required");
    }

    [Test]
    public async Task Put_twoConcurrentEditsAtTheSameRevision_oneSucceedsAndOneConflicts()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var a = app.CreateClientAs(Persona.ContentOwner);
        using var b = app.CreateClientAs(Persona.Maintainer);

        var responses = await Task.WhenAll(
            PutAsync(a, doc.DocumentId, ValidBody("From A"), "\"1\""),
            PutAsync(b, doc.DocumentId, ValidBody("From B"), "\"1\""));

        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.OK)).IsEqualTo(1);
        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed)).IsEqualTo(1);
        await Assert.That(await app.ScalarAsync<int>("SELECT revision FROM documents.documents WHERE id = $1", doc.DocumentId)).IsEqualTo(2);
    }

    [Test]
    public async Task Put_archivedDocument_succeeds()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PostActionAsync(client, doc.DocumentId, "archive")).EnsureSuccessStatusCode();

        var response = await PutAsync(client, doc.DocumentId, ValidBody("Renamed while archived"), "\"1\"");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("archived");
        await Assert.That(json.GetProperty("title").GetString()).IsEqualTo("Renamed while archived");
    }

    [Test]
    public async Task Put_unknownDocument_returns404()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await PutAsync(client, Guid.NewGuid(), ValidBody(), "\"1\"");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await CodeAsync(response)).IsEqualTo("documents.not_found");
    }

    [Test]
    public async Task Put_afterIngestionFinishesBetweenGetAndPut_stillSucceedsWithTheEarlierETag()
    {
        // The generator is unreachable, so the run stays queued while the owner reads the document.
        await using var app = await StartWithoutEmbeddingsAsync(postgres);
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);
        var read = await client.GetAsync($"/api/documents/{doc.DocumentId}");
        var etag = read.Headers.ETag!.Tag;

        app.Generator.Available = true;
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        var response = await PutAsync(client, doc.DocumentId, ValidBody("Edited"), etag);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo(doc.VersionId);
    }
}
