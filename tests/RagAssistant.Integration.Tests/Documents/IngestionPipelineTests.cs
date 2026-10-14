using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 007, AC-1 to AC-3, AC-9 to AC-14, AC-17, AC-18.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class IngestionPipelineTests(PostgresFixture postgres)
{
    private static readonly string[] SuccessfulEvents =
    [
        "Queued/Completed",
        "Extracting/Started", "Extracting/Completed",
        "Chunking/Started", "Chunking/Completed",
        "Embedding/Started", "Embedding/Completed",
        "Storing/Started", "Storing/Completed",
        "Completed/Completed",
    ];

    [Test]
    public async Task Ingest_importedMarkdown_indexesVersionAndStoresPassages()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();

        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var version = (await app.RowsAsync(
            "SELECT status, passage_count, embedding_model, embedding_dimensions, indexed_at FROM documents.document_versions WHERE id = $1",
            doc.VersionId)).Single();
        await Assert.That(version[0]).IsEqualTo("Indexed");
        await Assert.That(version[1]).IsEqualTo(2);
        await Assert.That(version[2]).IsEqualTo("bge-m3");
        await Assert.That(version[3]).IsEqualTo(1024);
        await Assert.That(version[4]).IsNotNull();

        await Assert.That(await app.ScalarAsync<Guid>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo(doc.VersionId);
        await Assert.That(await app.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId)).IsEqualTo(2L);
        await Assert.That(await app.ScalarAsync<long>(
                "SELECT count(*) FROM documents.passages WHERE version_id = $1 AND vector_dims(embedding) = 1024", doc.VersionId))
            .IsEqualTo(2L);

        var run = (await app.RowsAsync("SELECT current_stage, started_at, finished_at FROM documents.ingestion_runs WHERE id = $1", doc.RunId)).Single();
        await Assert.That(run[0]).IsEqualTo("Completed");
        await Assert.That(run[1]).IsNotNull();
        await Assert.That(run[2]).IsNotNull();
    }

    [Test]
    public async Task Ingest_providerReportsAModel_recordsItOnTheVersion()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, generator: new FakeEmbeddingGenerator { ReportedModel = "bge-m3:latest" });
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That(await app.ScalarAsync<string>("SELECT embedding_model FROM documents.document_versions WHERE id = $1", doc.VersionId))
            .IsEqualTo("bge-m3:latest");
    }

    [Test]
    public async Task Ingest_successfulRun_writesTheStageEventsInOrderWithIncreasingIds()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var events = await app.EventsAsync(doc.RunId);
        var ids = await app.EventIdsAsync(doc.RunId);

        await Assert.That(events).IsEquivalentTo(SuccessfulEvents);
        await Assert.That(ids.SequenceEqual(ids.Order())).IsTrue();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    [Test]
    public async Task Ingest_secondVersion_supersedesTheFirstAndKeepsItsPassages()
    {
        var name = $"ing_supersede_{Guid.NewGuid():N}";
        ImportedDocument doc;
        Guid run2;
        await using (var first = await IngestionHarness.StartAsync(postgres, name))
        {
            doc = await first.ImportAsync();
            await first.WaitForRunAsync(doc.RunId, "Succeeded");
            run2 = await first.AddQueuedVersionAsync(doc.DocumentId, 2, IngestionHarness.UniqueMarkdown("Updated text."));
        }

        // A restart is what picks up a run that was queued while the worker was not looking.
        await using var restarted = await IngestionHarness.StartAsync(postgres, name);
        await restarted.WaitForRunAsync(run2, "Succeeded");

        var versions = await restarted.RowsAsync("SELECT number, status FROM documents.document_versions WHERE document_id = $1 ORDER BY number", doc.DocumentId);
        await Assert.That(versions.Select(v => $"{v[0]}:{v[1]}").ToArray()).IsEquivalentTo(["1:Superseded", "2:Indexed"]);
        await Assert.That(await restarted.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId)).IsEqualTo(2L);
        await Assert.That(await restarted.ScalarAsync<Guid>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsNotEqualTo(doc.VersionId);
    }

    [Test]
    public async Task Ingest_failedSecondVersion_leavesThePreviousVersionActive()
    {
        var name = $"ing_keepactive_{Guid.NewGuid():N}";
        ImportedDocument doc;
        Guid run2;
        await using (var first = await IngestionHarness.StartAsync(postgres, name))
        {
            doc = await first.ImportAsync();
            await first.WaitForRunAsync(doc.RunId, "Succeeded");
            run2 = await first.AddQueuedVersionAsync(doc.DocumentId, 2, IngestionHarness.UniqueMarkdown());
        }

        await using var failing = await IngestionHarness.StartAsync(
            postgres, name, new FakeEmbeddingGenerator { AlwaysFail = true }, new Dictionary<string, string> { ["Documents:Ingestion:EmbeddingMaxRetries"] = "1" });
        await failing.WaitForRunAsync(run2, "Failed");

        await Assert.That(await failing.ScalarAsync<Guid>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId))
            .IsEqualTo(doc.VersionId);
        await Assert.That(await failing.ScalarAsync<string>("SELECT status FROM documents.document_versions WHERE id = $1", doc.VersionId))
            .IsEqualTo("Indexed");
        await Assert.That(await failing.ScalarAsync<string>(
                "SELECT status FROM documents.document_versions WHERE document_id = $1 AND number = 2", doc.DocumentId))
            .IsEqualTo("Failed");
    }

    [Test]
    public async Task Ingest_passageWithHeadings_sendsPathAndTextToTheGenerator()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync("# Handbook\n\n## Leave\n\nTwenty days per year.\n");
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var inputs = app.Generator.Batches.SelectMany(b => b).ToArray();

        await Assert.That(inputs).IsEquivalentTo(["Handbook > Leave\n\nTwenty days per year."]);
    }

    [Test]
    public async Task Ingest_textWithoutHeadings_sendsTheTextOnly()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync($"Just a note {Guid.NewGuid()}\n", "note.txt");
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var inputs = app.Generator.Batches.SelectMany(b => b).ToArray();

        await Assert.That(inputs.Length).IsEqualTo(1);
        await Assert.That(inputs[0]).StartsWith("Just a note");
    }

    [Test]
    public async Task Ingest_morePassagesThanBatchSize_callsTheGeneratorInBatches()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { ["Documents:Ingestion:EmbeddingBatchSize"] = "2" });
        var markdown = $"# Doc {Guid.NewGuid()}\n\n" + string.Concat(Enumerable.Range(1, 5).Select(n => $"## Section {n}\n\nBody of section {n}.\n\n"));
        var doc = await app.ImportAsync(markdown);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That(app.Generator.Batches.Select(b => b.Length).ToArray()).IsEquivalentTo([2, 2, 1]);
    }

    [Test]
    public async Task Ingest_generatorFailsTwice_retriesAndSucceeds()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        app.Generator.FailNext(2);
        var doc = await app.ImportAsync();

        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That(app.Generator.BatchCalls).IsEqualTo(3);
    }

    [Test]
    public async Task Ingest_generatorAlwaysFails_failsTheRunWithoutStoringAnything()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { ["Documents:Ingestion:EmbeddingMaxRetries"] = "2" });
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();

        await app.WaitForRunAsync(doc.RunId, "Failed");

        var run = (await app.RowsAsync(
            "SELECT current_stage, error_code, error_message FROM documents.ingestion_runs WHERE id = $1", doc.RunId)).Single();
        await Assert.That(run[0]).IsEqualTo("Embedding");
        await Assert.That(run[1]).IsEqualTo("documents.embedding_unavailable");
        await Assert.That(run[2]).IsNotNull();
        await Assert.That(app.Generator.BatchCalls).IsEqualTo(3);
        await Assert.That((await app.EventsAsync(doc.RunId))[^1]).IsEqualTo("Embedding/Failed");
        await Assert.That(await app.ScalarAsync<string>("SELECT status FROM documents.document_versions WHERE id = $1", doc.VersionId)).IsEqualTo("Failed");
        await Assert.That(await app.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId)).IsEqualTo(0L);
        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId)).IsNull();
    }

    [Test]
    [Arguments(1024, 1)]
    [Arguments(512, null)]
    public async Task Ingest_wrongVectorCountOrLength_failsAfterOneCallWithoutRetry(int vectorLength, int? returnedCount)
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        app.Generator.VectorLength = vectorLength;
        app.Generator.ReturnedCount = returnedCount;
        var doc = await app.ImportAsync($"# T {Guid.NewGuid()}\n\n## A\n\nfirst\n\n## B\n\nsecond\n");

        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(await app.ScalarAsync<string>("SELECT error_code FROM documents.ingestion_runs WHERE id = $1", doc.RunId))
            .IsEqualTo("documents.embedding_dimension_mismatch");
        await Assert.That(app.Generator.BatchCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Ingest_fileWithOnlyHeadings_failsWithNoPassages()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync($"# Title {Guid.NewGuid()}\n\n## Empty\n\n### Also empty\n");

        await app.WaitForRunAsync(doc.RunId, "Failed");

        var run = (await app.RowsAsync("SELECT current_stage, error_code FROM documents.ingestion_runs WHERE id = $1", doc.RunId)).Single();
        await Assert.That(run[0]).IsEqualTo("Chunking");
        await Assert.That(run[1]).IsEqualTo("documents.no_passages");
        await Assert.That(app.Generator.BatchCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Ingest_unexpectedException_failsWithAContentFreeMessage()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        app.Generator.ThrowUnexpected = true;
        var doc = await app.ImportAsync();

        await app.WaitForRunAsync(doc.RunId, "Failed");

        var run = (await app.RowsAsync("SELECT error_code, error_message FROM documents.ingestion_runs WHERE id = $1", doc.RunId)).Single();
        await Assert.That(run[0]).IsEqualTo("documents.ingestion_error");
        await Assert.That(run[1]).IsEqualTo("Unexpected error; see logs");
    }

    [Test]
    public async Task Ingest_modelUnavailable_keepsTheRunQueuedUntilItComesBack()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, generator: new FakeEmbeddingGenerator { Available = false });
        var doc = await app.ImportAsync();

        await Task.Delay(500);
        await Assert.That(await app.RunStatusAsync(doc.RunId)).IsEqualTo("Queued");
        await Assert.That(app.Generator.BatchCalls).IsEqualTo(0);

        app.Generator.Available = true;
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That((await app.EventsAsync(doc.RunId)).Any(e => e.EndsWith("/Failed", StringComparison.Ordinal))).IsFalse();
    }
}
