using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 007, AC-26, AC-27, AC-29.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class IngestionReadAuthAndTelemetryTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> NoRetries = new() { ["Documents:Ingestion:EmbeddingMaxRetries"] = "0" };

    private static async Task<ImportedDocument> ImportFailedAsync(IngestionHarness app)
    {
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        app.Generator.AlwaysFail = false;
        return doc;
    }

    [Test]
    public async Task Get_failedDocument_showsFailedStatusAndTheRunErrorToAKnowledgeSeeker()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        var doc = await ImportFailedAsync(app);
        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{doc.DocumentId}");

        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(body.GetProperty("activeVersionId").ValueKind).IsEqualTo(JsonValueKind.Null);
        var run = body.GetProperty("latestRun");
        await Assert.That(run.GetProperty("id").GetGuid()).IsEqualTo(doc.RunId);
        await Assert.That(run.GetProperty("attempt").GetInt32()).IsEqualTo(1);
        await Assert.That(run.GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(run.GetProperty("stage").GetString()).IsEqualTo("embedding");
        await Assert.That(run.GetProperty("errorCode").GetString()).IsEqualTo("documents.embedding_unavailable");
        await Assert.That(run.GetProperty("errorMessage").GetString()).IsNotNull();
        await Assert.That(run.GetProperty("finishedAt").ValueKind).IsNotEqualTo(JsonValueKind.Null);
        await Assert.That(body.GetProperty("latestVersion").GetProperty("passageCount").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Get_indexedDocument_showsActiveStatusActiveVersionAndPassageCount()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{doc.DocumentId}");

        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("active");
        await Assert.That(body.GetProperty("activeVersionId").GetGuid()).IsEqualTo(doc.VersionId);
        await Assert.That(body.GetProperty("latestVersion").GetProperty("passageCount").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("latestRun").GetProperty("status").GetString()).IsEqualTo("succeeded");
        await Assert.That(body.GetProperty("latestRun").GetProperty("stage").GetString()).IsEqualTo("completed");
    }

    [Test]
    public async Task RetryAndEvents_withoutToken_return401()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.Anonymous);

        var retry = await client.PostAsync($"/api/documents/{doc.DocumentId}/ingestion/retry", null);
        var events = await client.GetAsync($"/api/documents/{doc.DocumentId}/ingestion/events");

        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(events.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task RetryAndEvents_asKnowledgeSeeker_return403()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);

        var retry = await client.PostAsync($"/api/documents/{doc.DocumentId}/ingestion/retry", null);
        var events = await client.GetAsync($"/api/documents/{doc.DocumentId}/ingestion/events");

        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(events.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    [Arguments(Persona.ContentOwner)]
    [Arguments(Persona.Maintainer)]
    public async Task RetryAndEvents_asContentOwnerOrMaintainer_return202And200(Persona persona)
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        var doc = await ImportFailedAsync(app);
        using var client = app.CreateClientAs(persona);

        var events = await client.GetAsync($"/api/documents/{doc.DocumentId}/ingestion/events", HttpCompletionOption.ResponseHeadersRead);
        var retry = await client.PostAsync($"/api/documents/{doc.DocumentId}/ingestion/retry", null);

        await Assert.That(events.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task Telemetry_successAndFailure_recordCountersHistogramAndSpans()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
        var longs = new ConcurrentBag<(string Name, long Value, string? Reason)>();
        var durations = new ConcurrentBag<double>();
        var stopped = new ConcurrentBag<Activity>();

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
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            longs.Add((instrument.Name, value, reason));
        });
        meters.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "documents.ingestion.duration")
            {
                durations.Add(value);
            }
        });
        meters.Start();

        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RagAssistant.Documents",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(activities);

        var ok = await app.ImportAsync();
        await app.WaitForRunAsync(ok.RunId, "Succeeded");
        app.Generator.AlwaysFail = true;
        var bad = await app.ImportAsync();
        await app.WaitForRunAsync(bad.RunId, "Failed");

        await IngestionHarness.WaitUntilAsync(
            () => Task.FromResult(longs.Any(m => m.Name == "documents.ingestion.completed") && longs.Any(m => m.Name == "documents.ingestion.failed")
                && stopped.Count(a => a.OperationName == "ingestion.run" && (a.GetTagItem("run.id") is Guid id) && (id == ok.RunId || id == bad.RunId)) == 2),
            "metrics and spans");

        await Assert.That(longs.Where(m => m.Name == "documents.ingestion.completed").Sum(m => m.Value)).IsEqualTo(1L);
        await Assert.That(longs.Where(m => m.Name == "documents.passages.created").Sum(m => m.Value)).IsEqualTo(2L);
        var failed = longs.Single(m => m.Name == "documents.ingestion.failed");
        await Assert.That(failed.Value).IsEqualTo(1L);
        await Assert.That(failed.Reason).IsEqualTo("documents.embedding_unavailable");
        await Assert.That(durations.Count).IsEqualTo(2);

        var run = stopped.Single(a => a.OperationName == "ingestion.run" && a.GetTagItem("run.id") is Guid id && id == ok.RunId);
        var stages = stopped.Where(a => a.OperationName == "ingestion.stage" && a.ParentSpanId == run.SpanId)
            .Select(a => (string)a.GetTagItem("stage")!).ToArray();
        await Assert.That(stages).IsEquivalentTo(["extracting", "chunking", "embedding", "storing"]);
    }
}
