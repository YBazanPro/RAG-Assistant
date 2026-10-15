using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 009, AC-20 to AC-22.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class DocumentLifecycleAuthAndTelemetryTests(PostgresFixture postgres)
{
    private static Task<HttpResponseMessage>[] AllFive(HttpClient client, Guid id) =>
    [
        PutAsync(client, id, ValidBody(), "\"1\""),
        UploadVersionAsync(client, id, Markdown()),
        PostActionAsync(client, id, "reprocess"),
        PostActionAsync(client, id, "archive"),
        PostActionAsync(client, id, "restore"),
    ];

    [Test]
    public async Task Lifecycle_asAnonymous_returns401ForAllFiveEndpoints()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.Anonymous);

        var responses = await Task.WhenAll(AllFive(client, doc.DocumentId));

        await Assert.That(responses.All(r => r.StatusCode == HttpStatusCode.Unauthorized)).IsTrue();
    }

    [Test]
    public async Task Lifecycle_asKnowledgeSeeker_returns403ForAllFiveEndpoints()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);

        var responses = await Task.WhenAll(AllFive(client, doc.DocumentId));

        await Assert.That(responses.All(r => r.StatusCode == HttpStatusCode.Forbidden)).IsTrue();
    }

    [Test]
    [Arguments(Persona.ContentOwner)]
    [Arguments(Persona.Maintainer)]
    public async Task Lifecycle_asContentOwnerOrMaintainer_succeeds(Persona persona)
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(persona);

        var put = await PutAsync(client, doc.DocumentId, ValidBody("Renamed"), "\"1\"");
        var upload = await UploadVersionAsync(client, doc.DocumentId, Markdown());
        await app.WaitForRunAsync((await JsonAsync(upload)).GetProperty("ingestionRunId").GetGuid(), "Succeeded");
        var reprocess = await PostActionAsync(client, doc.DocumentId, "reprocess");
        await app.WaitForRunAsync((await JsonAsync(reprocess)).GetProperty("ingestionRunId").GetGuid(), "Succeeded");
        var archive = await PostActionAsync(client, doc.DocumentId, "archive");
        var restore = await PostActionAsync(client, doc.DocumentId, "restore");

        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(upload.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(reprocess.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(archive.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Schema_revisionColumn_isNotNullWithDefaultOne()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);

        var rows = await app.RowsAsync(
            "SELECT is_nullable, column_default FROM information_schema.columns WHERE table_schema = 'documents' AND table_name = 'documents' AND column_name = 'revision'");

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0][0]).IsEqualTo("NO");
        await Assert.That(rows[0][1]).IsEqualTo("1");
    }

    [Test]
    public async Task Telemetry_lifecycleActions_incrementCountersWithTags()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
        var measurements = new ConcurrentBag<(string Name, long Value, string? Origin, string? Reason)>();

        // Other tests run hosts in this process; only this host's meters (scoped to its factory) are counted.
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "RagAssistant.Documents" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? origin = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "origin")
                {
                    origin = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            measurements.Add((instrument.Name, value, origin, reason));
        });
        meters.Start();

        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (await PutAsync(client, doc.DocumentId, ValidBody("T"), "\"1\"")).EnsureSuccessStatusCode();
        await PutAsync(client, doc.DocumentId, ValidBody("T2"), "\"1\""); // stale: rejected
        var upload = await UploadVersionAsync(client, doc.DocumentId, Markdown());
        await app.WaitForRunAsync((await JsonAsync(upload)).GetProperty("ingestionRunId").GetGuid(), "Succeeded");
        var reprocess = await PostActionAsync(client, doc.DocumentId, "reprocess");
        await app.WaitForRunAsync((await JsonAsync(reprocess)).GetProperty("ingestionRunId").GetGuid(), "Succeeded");
        await PostActionAsync(client, doc.DocumentId, "archive");
        await PostActionAsync(client, doc.DocumentId, "restore");

        await Assert.That(measurements.Where(m => m.Name == "documents.metadata.updated").Sum(m => m.Value)).IsEqualTo(1L);
        await Assert.That(measurements.Where(m => m.Name == "documents.versions.created" && m.Origin == "upload").Sum(m => m.Value)).IsEqualTo(1L);
        await Assert.That(measurements.Where(m => m.Name == "documents.versions.created" && m.Origin == "reprocess").Sum(m => m.Value)).IsEqualTo(1L);
        await Assert.That(measurements.Where(m => m.Name == "documents.archived").Sum(m => m.Value)).IsEqualTo(1L);
        await Assert.That(measurements.Where(m => m.Name == "documents.restored").Sum(m => m.Value)).IsEqualTo(1L);
        await Assert.That(measurements.Single(m => m.Name == "documents.lifecycle.rejected").Reason).IsEqualTo("documents.edit_conflict");
    }
}
