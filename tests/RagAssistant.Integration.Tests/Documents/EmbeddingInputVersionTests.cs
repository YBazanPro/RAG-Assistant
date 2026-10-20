using System.Text.Json;
using RagAssistant.Integration.Tests.Chat;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 021, AC-9: versions embedded with an older input stay searchable, are counted by health and flagged in the version list,
// and a reprocess brings them up to date.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class EmbeddingInputVersionTests(PostgresFixture postgres)
{
    [Test]
    public async Task OutdatedActiveVersion_isCountedAndFlagged_untilReprocessed()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        await app.ExecuteAsync("UPDATE documents.document_versions SET embedding_input_version = 1 WHERE id = $1", doc.VersionId);
        using var admin = app.CreateClientAs(Persona.Maintainer);
        using var owner = app.CreateClientAs(Persona.ContentOwner);

        var (_, health) = await admin.GetJsonAsync("/api/admin/documents/health");
        var (_, versions) = await owner.GetJsonAsync($"/api/documents/{doc.DocumentId}/versions");

        await Assert.That(health.GetProperty("outdatedEmbeddings").GetInt32()).IsEqualTo(1);
        await Assert.That(Outdated(versions)).IsEquivalentTo([true]);

        var reprocess = await PostActionAsync(owner, doc.DocumentId, "reprocess");
        reprocess.EnsureSuccessStatusCode();
        var runId = JsonDocument.Parse(await reprocess.Content.ReadAsStringAsync()).RootElement.GetProperty("ingestionRunId").GetGuid();
        await app.WaitForRunAsync(runId, "Succeeded");

        (_, health) = await admin.GetJsonAsync("/api/admin/documents/health");
        (_, versions) = await owner.GetJsonAsync($"/api/documents/{doc.DocumentId}/versions");

        await Assert.That(health.GetProperty("outdatedEmbeddings").GetInt32()).IsEqualTo(0);
        // Newest first: the reprocessed version is current, the superseded one keeps its old input.
        await Assert.That(Outdated(versions).SequenceEqual([false, true])).IsTrue();
    }

    [Test]
    public async Task VersionNotEmbedded_isNeitherOutdatedNorCounted()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var seeded = await new CorpusSeeder(app.Db).AddDocumentAsync(new SeedDocument("Queued", "o", VersionStatus: "Queued"));
        using var admin = app.CreateClientAs(Persona.Maintainer);

        var (_, health) = await admin.GetJsonAsync("/api/admin/documents/health");
        var (_, versions) = await admin.GetJsonAsync($"/api/documents/{seeded.DocumentId}/versions");

        await Assert.That(health.GetProperty("outdatedEmbeddings").GetInt32()).IsEqualTo(0);
        await Assert.That(versions.GetProperty("items")[0].GetProperty("embeddingInputOutdated").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    private static bool[] Outdated(JsonElement versions) =>
        [.. versions.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("embeddingInputOutdated").GetBoolean())];
}
