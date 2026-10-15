using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 006, AC-17 (schema), AC-18 (configuration) and AC-19 (metrics).
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class DocumentsSchemaAndConfigTests(RagAssistantAppFactory factory)
{
    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [Test]
    public async Task Migrations_applied_createTheFiveTablesInTheDocumentsSchema()
    {
        var tables = await DocumentUploads.ScalarAsync<string>(
            Db,
            "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables " +
            "WHERE table_schema = 'documents' AND table_name <> '__EFMigrationsHistory'");

        await Assert.That(tables).IsEqualTo("document_versions,documents,ingestion_events,ingestion_runs,passages");
    }

    [Test]
    public async Task Migrations_applied_keepTheirHistoryInTheDocumentsSchema()
    {
        var count = await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM documents.\"__EFMigrationsHistory\"");

        await Assert.That(count).IsGreaterThanOrEqualTo(1L);
    }

    [Test]
    public async Task Migrations_applied_enableVectorAndCreateA1024WideEmbeddingColumn()
    {
        await Assert.That(await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM pg_extension WHERE extname = 'vector'")).IsEqualTo(1L);
        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db,
            "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a " +
            "WHERE a.attrelid = 'documents.passages'::regclass AND a.attname = 'embedding'")).IsEqualTo("vector(1024)");
    }

    [Test]
    public async Task Migrations_applied_createHnswCosineGinAndPartialUniqueIndexes()
    {
        var hnsw = await IndexDefinitionAsync("passages", "embedding");
        await Assert.That(hnsw).Contains("USING hnsw");
        await Assert.That(hnsw).Contains("vector_cosine_ops");

        await Assert.That(await IndexDefinitionAsync("documents", "tags")).Contains("USING gin");

        var unique = await IndexDefinitionAsync("documents", "latest_content_hash");
        await Assert.That(unique).Contains("UNIQUE");
        await Assert.That(unique).Contains("WHERE");
    }

    private Task<string?> IndexDefinitionAsync(string table, string column) =>
        DocumentUploads.ScalarAsync<string>(
            Db, "SELECT indexdef FROM pg_indexes WHERE schemaname = 'documents' AND tablename = $1 AND indexdef LIKE '%(' || $2 || '%'",
            table, column);

    [Test]
    public async Task Startup_embeddingDimensionsDifferFromTheColumn_fails()
    {
        await using var misconfigured = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Documents:Embeddings:Dimensions", "768"));

        await Assert.That(() => misconfigured.CreateClient()).Throws<OptionsValidationException>();
    }

    [Test]
    public async Task Metrics_importedAndRejected_eachIncrementOnce()
    {
        // A derived factory has its own IMeterFactory, so counts from tests running in parallel cannot leak in.
        await using var isolated = factory.WithWebHostBuilder(_ => { });
        var meterFactory = isolated.Services.GetRequiredService<IMeterFactory>();
        var measurements = new ConcurrentBag<(string Instrument, long Value, string Tag)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "RagAssistant.Documents" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.Length > 0 ? tags[0].Value?.ToString() ?? string.Empty : string.Empty)));
        listener.Start();

        using var client = isolated.CreateClientAs(Persona.ContentOwner);
        using var accepted = DocumentUploads.Form("a.md", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle());
        using var rejected = DocumentUploads.Form("a.docx", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle());
        var first = await client.PostAsync("/api/documents", accepted);
        var second = await client.PostAsync("/api/documents", rejected);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var recorded = measurements.ToArray();
        await Assert.That(recorded.Length).IsEqualTo(2);
        await Assert.That(recorded.Single(m => m.Instrument == "documents.imported").Tag).IsEqualTo("markdown");
        await Assert.That(recorded.Single(m => m.Instrument == "documents.import.rejected").Tag).IsEqualTo("documents.unsupported_extension");
    }
}
