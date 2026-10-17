using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 017, AC-16 to AC-20 and the Documents half of AC-22: corpus health, failures, the metadata-only private list, configuration.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class AdminDocumentsTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> NoRetries = new() { ["Documents:Ingestion:EmbeddingMaxRetries"] = "1" };

    private static async Task<(HttpStatusCode Status, string Raw, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, raw, raw.Length == 0 ? default : JsonDocument.Parse(raw).RootElement.Clone());
    }

    // AC-16
    [Test]
    public async Task Health_countsByStatusAndVisibility_andFlagsOnlyOldSharedActiveDocumentsAsStale()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var seeder = new CorpusSeeder(app.Db);
        var old = DateTimeOffset.UtcNow.AddDays(-200);
        await seeder.AddDocumentAsync(new SeedDocument("Old shared", "o", UpdatedAt: old));
        await seeder.AddDocumentAsync(new SeedDocument("New shared", "o"));
        await seeder.AddDocumentAsync(new SeedDocument("Old private", "o", UpdatedAt: old, Visibility: "Private", CreatedBy: "p1"));
        await seeder.AddDocumentAsync(new SeedDocument("Archived shared", "o", Archived: true, UpdatedAt: old));
        await seeder.AddDocumentAsync(new SeedDocument("Archived private", "o", Archived: true, Visibility: "Private", CreatedBy: "p2"));
        var failed = await seeder.AddDocumentAsync(new SeedDocument("Failed shared", "o", VersionStatus: "Failed"));
        await seeder.AddRunAsync(failed.VersionId, 1, "Failed", "Embedding", "embedding.failed", "boom");
        var queued = await seeder.AddDocumentAsync(new SeedDocument("Queued shared", "o", VersionStatus: "Queued"));
        await seeder.AddRunAsync(queued.VersionId, 1, "Queued", "Queued");
        using var admin = app.CreateClientAs(Persona.Maintainer);

        var (status, raw, body) = await GetAsync(admin, "/api/admin/documents/health");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var documents = body.GetProperty("documents");
        await Assert.That(documents.GetProperty("activeShared").GetInt32()).IsEqualTo(4);
        await Assert.That(documents.GetProperty("activePrivate").GetInt32()).IsEqualTo(1);
        await Assert.That(documents.GetProperty("archivedShared").GetInt32()).IsEqualTo(1);
        await Assert.That(documents.GetProperty("archivedPrivate").GetInt32()).IsEqualTo(1);
        var versions = body.GetProperty("versions");
        await Assert.That(versions.GetProperty("indexed").GetInt32()).IsEqualTo(5);
        await Assert.That(versions.GetProperty("failed").GetInt32()).IsEqualTo(1);
        await Assert.That(versions.GetProperty("queued").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("queuedVersions").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("oldestPendingSeconds").GetDouble()).IsGreaterThanOrEqualTo(0);
        await Assert.That(body.GetProperty("staleShared").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("staleAfterDays").GetInt32()).IsEqualTo(180);
        await Assert.That(raw).DoesNotContain("Old private");
        await Assert.That(raw).DoesNotContain("p1");
    }

    [Test]
    public async Task Health_staleAfterDaysIsConfigurable()
    {
        await using var app = await IngestionHarness.StartAsync(
            postgres, settings: new Dictionary<string, string> { ["Documents:Admin:StaleAfterDays"] = "7" });
        await new CorpusSeeder(app.Db).AddDocumentAsync(new SeedDocument("Ten days old", "o", UpdatedAt: DateTimeOffset.UtcNow.AddDays(-10)));
        using var admin = app.CreateClientAs(Persona.Maintainer);

        var (_, _, body) = await GetAsync(admin, "/api/admin/documents/health");

        await Assert.That(body.GetProperty("staleShared").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("staleAfterDays").GetInt32()).IsEqualTo(7);
        await Assert.That(body.GetProperty("oldestPendingSeconds").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    // AC-17 and AC-19
    [Test]
    public async Task Failures_aFailedSharedVersionCanBeRetried_andLeavesTheListOnceItSucceeds()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        using var admin = app.CreateClientAs(Persona.Maintainer);

        var (_, _, before) = await GetAsync(admin, "/api/admin/documents/failures");

        await Assert.That(before.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
        var row = before.GetProperty("items")[0];
        await Assert.That(row.GetProperty("documentId").GetGuid()).IsEqualTo(doc.DocumentId);
        await Assert.That(row.GetProperty("visibility").GetString()).IsEqualTo("shared");
        await Assert.That(row.GetProperty("canRetry").GetBoolean()).IsTrue();
        await Assert.That(row.GetProperty("attempt").GetInt32()).IsEqualTo(1);
        await Assert.That(row.GetProperty("errorCode").GetString()).IsNotNull();
        await Assert.That(row.GetProperty("stage").GetString()).IsNotNull();
        await Assert.That(row.GetProperty("failedAt").ValueKind).IsNotEqualTo(JsonValueKind.Null);

        app.Generator.AlwaysFail = false;
        using var retry = await admin.PostAsync($"/api/documents/{doc.DocumentId}/ingestion/retry", null);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var runId = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionRunId").GetGuid();
        await app.WaitForRunAsync(runId, "Succeeded");

        var (_, _, after) = await GetAsync(admin, "/api/admin/documents/failures");
        await Assert.That(after.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
    }

    // AC-18
    [Test]
    public async Task Failures_aPrivateDocumentShowsMetadataButCannotBeRetriedByAMaintainer()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var seeder = new CorpusSeeder(app.Db);
        var secret = await seeder.AddDocumentAsync(new SeedDocument("Secret plan", "o", VersionStatus: "Failed", Visibility: "Private", CreatedBy: "owner-sub"));
        await seeder.AddRunAsync(secret.VersionId, 1, "Failed", "Embedding", "embedding.failed", "boom");
        using var admin = app.CreateClientAs(Persona.Maintainer, subject: "maintainer-sub");

        var (_, raw, body) = await GetAsync(admin, "/api/admin/documents/failures");
        using var retry = await admin.PostAsync($"/api/documents/{secret.DocumentId}/ingestion/retry", null);

        var row = body.GetProperty("items")[0];
        await Assert.That(row.GetProperty("title").GetString()).IsEqualTo("Secret plan");
        await Assert.That(row.GetProperty("ownerSub").GetString()).IsEqualTo("owner-sub");
        await Assert.That(row.GetProperty("visibility").GetString()).IsEqualTo("private");
        await Assert.That(row.GetProperty("stage").GetString()).IsEqualTo("embedding");
        await Assert.That(row.GetProperty("errorCode").GetString()).IsEqualTo("embedding.failed");
        await Assert.That(row.GetProperty("canRetry").GetBoolean()).IsFalse();
        await Assert.That(raw).DoesNotContain("seed.md");
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // AC-20
    [Test]
    public async Task PrivateList_showsMetadataOnly_andNoOrdinaryReadPathOpensThem()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var seeder = new CorpusSeeder(app.Db);
        var one = await seeder.AddDocumentAsync(new SeedDocument("Private one", "o", Tags: ["hr"], Visibility: "Private", CreatedBy: "owner-1"));
        await seeder.AddDocumentAsync(new SeedDocument("Private two", "o", Visibility: "Private", CreatedBy: "owner-2"));
        await seeder.AddDocumentAsync(new SeedDocument("Shared one", "o"));
        using var admin = app.CreateClientAs(Persona.Maintainer, subject: "maintainer-sub");

        var (status, raw, body) = await GetAsync(admin, "/api/admin/documents/private");
        var ordinary = await GetAsync(admin, "/api/documents");
        var detail = await GetAsync(admin, $"/api/documents/{one.DocumentId}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
        var properties = body.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order().ToArray();
        await Assert.That(properties.SequenceEqual(["createdAt", "documentId", "latestVersionStatus", "ownerSub", "status", "title", "updatedAt"])).IsTrue();
        await Assert.That(raw).DoesNotContain("Shared one");
        await Assert.That(raw).DoesNotContain("hr");
        await Assert.That(ordinary.Raw).DoesNotContain("Private one");
        await Assert.That(ordinary.Raw).Contains("Shared one");
        await Assert.That(detail.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    // AC-22
    [Test]
    public async Task Configuration_showsTheEffectiveOptions_andNoPathOrSecret()
    {
        await using var app = await IngestionHarness.StartAsync(
            postgres, settings: new Dictionary<string, string> { ["Documents:Chunking:MaxTokens"] = "300", ["Documents:Chunking:MinTokens"] = "50" });
        using var admin = app.CreateClientAs(Persona.Maintainer);

        var (status, raw, body) = await GetAsync(admin, "/api/admin/documents/configuration");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("chunkMaxTokens").GetInt32()).IsEqualTo(300);
        await Assert.That(body.GetProperty("embeddingDimensions").GetInt32()).IsEqualTo(1024);
        await Assert.That(body.GetProperty("storage").GetString()).IsEqualTo("file system");
        await Assert.That(raw).DoesNotContain(app.StoragePath);
        await Assert.That(raw).DoesNotContain("Password");
        await Assert.That(raw).DoesNotContain("Host=");
    }
}
