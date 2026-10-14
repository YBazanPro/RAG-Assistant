using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 007, AC-21 to AC-25.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class IngestionStreamTests(PostgresFixture postgres)
{
    private static string EventsRoute(Guid documentId) => $"/api/documents/{documentId}/ingestion/events";

    private static async Task<List<SseItem<string>>> ReadAsync(
        HttpClient client, Guid documentId, string? lastEventId = null, Func<List<SseItem<string>>, bool>? stopWhen = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, EventsRoute(documentId));
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");

        var items = new List<SseItem<string>>();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
        {
            items.Add(item);
            if (stopWhen?.Invoke(items) == true)
            {
                break;
            }
        }

        return items;
    }

    private static List<SseItem<string>> Stages(IEnumerable<SseItem<string>> items) =>
        [.. items.Where(i => i.EventType == "ingestion.stage")];

    private static long EventId(SseItem<string> item) => long.Parse(item.EventId!, System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement Data(SseItem<string> item) => JsonDocument.Parse(item.Data).RootElement;

    [Test]
    public async Task Stream_whileRunInProgress_deliversStagesLiveAndEndsAfterTheTerminalEvent()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        app.Generator.HoldBatches();
        var doc = await app.ImportAsync();
        await app.Generator.WaitUntilHeldAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var reading = ReadAsync(client, doc.DocumentId);
        await Task.Delay(300);
        await Assert.That(reading.IsCompleted).IsFalse();
        app.Generator.Release();
        var items = Stages(await reading);

        var ids = await app.EventIdsAsync(doc.RunId);
        await Assert.That(items.Select(i => EventId(i)).ToArray()).IsEquivalentTo(ids);
        var last = Data(items[^1]);
        await Assert.That(last.GetProperty("stage").GetString()).IsEqualTo("completed");
        await Assert.That(last.GetProperty("status").GetString()).IsEqualTo("completed");
        await Assert.That(last.GetProperty("runId").GetGuid()).IsEqualTo(doc.RunId);
        await Assert.That(last.GetProperty("versionId").GetGuid()).IsEqualTo(doc.VersionId);
        await Assert.That(last.GetProperty("versionNumber").GetInt32()).IsEqualTo(1);
        await Assert.That(last.GetProperty("attempt").GetInt32()).IsEqualTo(1);
        await Assert.That(items.Select(i => Data(i).GetProperty("stage").GetString()).First()).IsEqualTo("queued");
    }

    [Test]
    public async Task Stream_terminalRun_replaysFromTheGivenEventIdAndTreatsGarbageAsAbsent()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        var ids = await app.EventIdsAsync(doc.RunId);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var all = Stages(await ReadAsync(client, doc.DocumentId));
        var afterMiddle = Stages(await ReadAsync(client, doc.DocumentId, ids[3].ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var afterLast = Stages(await ReadAsync(client, doc.DocumentId, ids[^1].ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var invalid = Stages(await ReadAsync(client, doc.DocumentId, "not-a-number"));

        await Assert.That(all.Count).IsEqualTo(ids.Count);
        await Assert.That(afterMiddle.Select(i => EventId(i)).ToArray()).IsEquivalentTo(ids.Skip(4).ToArray());
        await Assert.That(afterLast.Count).IsEqualTo(0);
        await Assert.That(invalid.Count).IsEqualTo(ids.Count);
    }

    [Test]
    public async Task Stream_afterRetry_deliversTheWholeNewRunToAClientHoldingAnOldId()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { ["Documents:Ingestion:EmbeddingMaxRetries"] = "1" });
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        var oldLast = (await app.EventIdsAsync(doc.RunId))[^1];

        app.Generator.AlwaysFail = false;
        using var client = app.CreateClientAs(Persona.ContentOwner);
        var retry = await (await client.PostAsync($"/api/documents/{doc.DocumentId}/ingestion/retry", null)).Content.ReadFromJsonAsync<JsonElement>();
        var run2 = retry.GetProperty("ingestionRunId").GetGuid();

        var items = Stages(await ReadAsync(client, doc.DocumentId, oldLast.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        await Assert.That(items.Count).IsEqualTo((await app.EventIdsAsync(run2)).Count);
        await Assert.That(Data(items[0]).GetProperty("runId").GetGuid()).IsEqualTo(run2);
        await Assert.That(Data(items[0]).GetProperty("attempt").GetInt32()).IsEqualTo(2);
        await Assert.That(Data(items[0]).GetProperty("message").GetString()).IsEqualTo("Retry requested");
        await Assert.That(Data(items[^1]).GetProperty("stage").GetString()).IsEqualTo("completed");
    }

    [Test]
    public async Task Stream_failedRun_endsWithTheFailedEvent()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: new Dictionary<string, string> { ["Documents:Ingestion:EmbeddingMaxRetries"] = "1" });
        app.Generator.AlwaysFail = true;
        var doc = await app.ImportAsync();
        await app.WaitForRunAsync(doc.RunId, "Failed");
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var items = Stages(await ReadAsync(client, doc.DocumentId));

        await Assert.That(Data(items[^1]).GetProperty("stage").GetString()).IsEqualTo("embedding");
        await Assert.That(Data(items[^1]).GetProperty("status").GetString()).IsEqualTo("failed");
    }

    [Test]
    public async Task Stream_idleWhileWorkerPaused_sendsHeartbeatsEveryIntervalOnTheControlledClock()
    {
        var clock = new ManualTimeProvider();
        await using var app = await IngestionHarness.StartAsync(
            postgres, generator: new FakeEmbeddingGenerator { Available = false }, clock: clock);
        var doc = await app.ImportAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var reading = ReadAsync(client, doc.DocumentId, stopWhen: seen => seen.Count(i => i.EventType == "heartbeat") >= 2);
        var beforeFirstInterval = clock.Elapsed;
        while (!reading.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
        }

        var items = await reading;
        // The default heartbeat interval is 15 s: two heartbeats cannot have been sent in less than 30 s of clock time.
        await Assert.That(clock.Elapsed - beforeFirstInterval).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));

        await Assert.That(items[0].EventType).IsEqualTo("ingestion.stage");
        await Assert.That(items.Count(i => i.EventType == "heartbeat")).IsEqualTo(2);
        await Assert.That(items.First(i => i.EventType == "heartbeat").Data).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Stream_unknownDocument_returns404BeforeAnyStream()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await client.GetAsync(EventsRoute(Guid.NewGuid()));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.not_found");
    }

    [Test]
    public async Task Stream_clientDisconnectsMidRun_doesNotAffectTheRun()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        app.Generator.HoldBatches();
        var doc = await app.ImportAsync();
        await app.Generator.WaitUntilHeldAsync();
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var seen = await ReadAsync(client, doc.DocumentId, stopWhen: items => items.Count >= 1);
        app.Generator.Release();

        await Assert.That(seen.Count).IsEqualTo(1);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
    }
}
