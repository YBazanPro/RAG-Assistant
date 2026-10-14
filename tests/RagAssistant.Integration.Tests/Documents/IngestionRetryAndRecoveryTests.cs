using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 007, AC-15, AC-16, AC-19, AC-20.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class IngestionRetryAndRecoveryTests(PostgresFixture postgres)
{
    private static string RetryRoute(Guid documentId) => $"/api/documents/{documentId}/ingestion/retry";

    private static async Task<ImportedDocument> ImportFailedAsync(IngestionHarness app)
    {
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        return doc;
    }

    [Test]
    public async Task Retry_failedVersion_returns202AndIndexesWithAttemptTwo()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { ["Documents:Ingestion:EmbeddingMaxRetries"] = "0" });
        var doc = await ImportFailedAsync(app);
        app.Generator.AlwaysFail = false;
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await client.PostAsync(RetryRoute(doc.DocumentId), null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(body.GetProperty("attempt").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("queued");
        await Assert.That(body.GetProperty("versionId").GetGuid()).IsEqualTo(doc.VersionId);

        var run2 = body.GetProperty("ingestionRunId").GetGuid();
        await app.WaitForRunAsync(run2, "Succeeded");
        await Assert.That(await app.ScalarAsync<string>("SELECT status FROM documents.document_versions WHERE id = $1", doc.VersionId)).IsEqualTo("Indexed");
        await Assert.That((await app.EventsAsync(run2))[0]).IsEqualTo("Queued/Completed");
        await Assert.That(await app.ScalarAsync<string>(
                "SELECT message FROM documents.ingestion_events WHERE run_id = $1 ORDER BY id LIMIT 1", run2))
            .IsEqualTo("Retry requested");
    }

    [Test]
    public async Task Retry_indexedDocument_returns409NotRetryable()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await client.PostAsync(RetryRoute(doc.DocumentId), null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.not_retryable");
    }

    [Test]
    public async Task Retry_queuedDocument_returns409NotRetryable()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, generator: new FakeEmbeddingGenerator { Available = false });
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await client.PostAsync(RetryRoute(doc.DocumentId), null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Retry_unknownDocument_returns404()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await client.PostAsync(RetryRoute(Guid.NewGuid()), null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.not_found");
    }

    [Test]
    public async Task Retry_twoConcurrentRequests_acceptExactlyOne()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { ["Documents:Ingestion:EmbeddingMaxRetries"] = "0" });
        var doc = await ImportFailedAsync(app);
        using var first = app.CreateClientAs(Persona.ContentOwner);
        using var second = app.CreateClientAs(Persona.ContentOwner);

        var responses = await Task.WhenAll(first.PostAsync(RetryRoute(doc.DocumentId), null), second.PostAsync(RetryRoute(doc.DocumentId), null));

        await Assert.That(responses.Select(r => (int)r.StatusCode).Order().ToArray()).IsEquivalentTo([202, 409]);
        await Assert.That(await app.ScalarAsync<long>(
                "SELECT count(*) FROM documents.ingestion_runs WHERE version_id = $1", doc.VersionId))
            .IsEqualTo(2L);
    }

    [Test]
    public async Task Restart_runsLeftRunningAreRequeuedWithAnEventAndProcessedInQueueOrder()
    {
        var name = $"ing_restart_{Guid.NewGuid():N}";
        var docs = new List<ImportedDocument>();
        await using (var paused = await IngestionHarness.StartAsync(postgres, name, new FakeEmbeddingGenerator { Available = false }))
        {
            for (var i = 0; i < 3; i++)
            {
                docs.Add(await paused.ImportAsync());
                await Task.Delay(10);
            }

            // What a crash mid-run leaves behind.
            await paused.ExecuteAsync("UPDATE documents.ingestion_runs SET status = 'Running', current_stage = 'Embedding', started_at = now() WHERE id = $1", docs[0].RunId);
            await paused.ExecuteAsync("UPDATE documents.document_versions SET status = 'Processing' WHERE id = $1", docs[0].VersionId);
        }

        await using var restarted = await IngestionHarness.StartAsync(postgres, name);
        foreach (var doc in docs)
        {
            await restarted.WaitForRunAsync(doc.RunId, "Succeeded");
        }

        var requeued = await restarted.RowsAsync(
            "SELECT message FROM documents.ingestion_events WHERE run_id = $1 AND stage = 'Queued' AND status = 'Started'", docs[0].RunId);
        await Assert.That(requeued.Select(r => (string)r[0]!).ToArray()).IsEquivalentTo(["Re-queued after restart"]);
        await Assert.That(await restarted.ScalarAsync<int>("SELECT attempt FROM documents.ingestion_runs WHERE id = $1", docs[0].RunId)).IsEqualTo(1);

        var byQueue = (await restarted.RowsAsync("SELECT id FROM documents.ingestion_runs ORDER BY queued_at")).Select(r => (Guid)r[0]!).ToArray();
        var byStart = (await restarted.RowsAsync("SELECT id FROM documents.ingestion_runs ORDER BY started_at")).Select(r => (Guid)r[0]!).ToArray();
        await Assert.That(byStart).IsEquivalentTo(byQueue);
    }

    [Test]
    public async Task Worker_twoConsumers_processEachRunExactlyOnce()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { ["Documents:Ingestion:MaxConcurrentRuns"] = "2" });
        var docs = new List<ImportedDocument>();
        for (var i = 0; i < 6; i++)
        {
            docs.Add(await app.ImportAsync());
        }

        foreach (var doc in docs)
        {
            await app.WaitForRunAsync(doc.RunId, "Succeeded");
        }

        foreach (var doc in docs)
        {
            await Assert.That(await app.ScalarAsync<long>(
                    "SELECT count(*) FROM documents.ingestion_events WHERE run_id = $1 AND stage = 'Extracting' AND status = 'Started'", doc.RunId))
                .IsEqualTo(1L);
            await Assert.That(await app.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId)).IsEqualTo(2L);
        }
    }
}
