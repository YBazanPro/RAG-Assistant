using Microsoft.Extensions.Options;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 007: crash recovery, shutdown behavior and startup validation (AC-19, AC-28 and the "API stops during a run" edge case).
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class IngestionResilienceTests(PostgresFixture postgres)
{
    [Test]
    public async Task Worker_infrastructureErrorWhileFailingARun_handsTheRunBackInsteadOfStrandingIt()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, generator: new FakeEmbeddingGenerator { Available = false });

        // The first insert of a "Failed" event raises inside the database, which is exactly the write FailAsync does after
        // an unexpected stage error. A sequence survives the rollback, so only that first attempt is poisoned.
        await app.ExecuteAsync("CREATE SEQUENCE documents.test_poison_seq");
        await app.ExecuteAsync(
            """
            CREATE FUNCTION documents.test_poison_fn() RETURNS trigger AS $$
            BEGIN
                IF NEW.status = 'Failed' AND nextval('documents.test_poison_seq') = 1 THEN
                    RAISE EXCEPTION 'poisoned once';
                END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql
            """);
        await app.ExecuteAsync(
            "CREATE TRIGGER test_poison BEFORE INSERT ON documents.ingestion_events FOR EACH ROW EXECUTE FUNCTION documents.test_poison_fn()");

        app.Generator.ThrowUnexpected = true;
        var doc = await app.ImportAsync();
        app.Generator.Available = true;

        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(app.Generator.BatchCalls).IsEqualTo(2);
        await Assert.That(await app.ScalarAsync<string>("SELECT error_code FROM documents.ingestion_runs WHERE id = $1", doc.RunId))
            .IsEqualTo("documents.ingestion_error");
        await Assert.That(await app.ScalarAsync<long>(
                "SELECT count(*) FROM documents.ingestion_events WHERE run_id = $1 AND message = 'Re-queued after an error'", doc.RunId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Shutdown_midRun_writesNothingAndTheNextStartFinishesTheRun()
    {
        var name = $"ing_shutdown_{Guid.NewGuid():N}";
        ImportedDocument doc;
        await using (var stopping = await IngestionHarness.StartAsync(postgres, name))
        {
            stopping.Generator.HoldBatches();
            doc = await stopping.ImportAsync();
            await stopping.Generator.WaitUntilHeldAsync();
        }

        await using var inspecting = await IngestionHarness.StartAsync(postgres, name, new FakeEmbeddingGenerator { Available = false });
        await Assert.That((await inspecting.EventsAsync(doc.RunId)).Any(e => e.EndsWith("/Failed", StringComparison.Ordinal))).IsFalse();
        await Assert.That(await inspecting.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId)).IsEqualTo(0L);
        await Assert.That(await inspecting.RunStatusAsync(doc.RunId)).IsEqualTo("Queued");
        await Assert.That(await inspecting.ScalarAsync<long>(
                "SELECT count(*) FROM documents.ingestion_events WHERE run_id = $1 AND message = 'Re-queued after restart'", doc.RunId))
            .IsEqualTo(1L);

        inspecting.Generator.Available = true;
        await inspecting.WaitForRunAsync(doc.RunId, "Succeeded");
    }

    [Test]
    [Arguments("Documents:Chunking:OverlapTokens", "400")]
    [Arguments("Documents:Chunking:MinTokens", "400")]
    [Arguments("Documents:Ingestion:EmbeddingBatchSize", "0")]
    [Arguments("Documents:Ingestion:EmbeddingMaxRetries", "0")]
    public async Task Host_invalidIngestionOptions_failsToStart(string key, string value)
    {
        await Assert.That(async () =>
            {
                await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { [key] = value });
            })
            .Throws<OptionsValidationException>();
    }
}
