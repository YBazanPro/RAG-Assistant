using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.Modules.Documents.Tests;
using static RagAssistant.Modules.Documents.Tests.TestPdf;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 010: progress events inside long stages (AC-21, AC-22) and the PDF metrics (AC-20).
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class IngestionProgressTests(PostgresFixture postgres)
{
    private sealed record StageRow(string Stage, string Status, int? Current, int? Total);

    private static readonly Dictionary<string, string> SmallPassages = new()
    {
        ["Documents:Chunking:MaxTokens"] = "60",
        ["Documents:Chunking:OverlapTokens"] = "5",
    };

    private static string Sentence(int page, int i) => $"Page{page} sentence number {i} talks about budget approvals and travel limits.";

    private static PageSpec TextPage(int page) => Page(Body(700, [.. Enumerable.Range(0, 30).Select(i => Sentence(page, i))]));

    private static async Task<List<StageRow>> StageRowsAsync(IngestionHarness app, Guid runId) =>
        [.. (await app.RowsAsync(
            "SELECT stage, status, current, total FROM documents.ingestion_events WHERE run_id = $1 ORDER BY id", runId))
            .Select(r => new StageRow((string)r[0]!, (string)r[1]!, r[2] as int?, r[3] as int?))];

    private static async Task<ImportedDocument> ImportPdfAsync(IngestionHarness app, byte[] pdf)
    {
        using var client = app.CreateClientAs(Persona.ContentOwner);
        using var form = TestPdfs.Form(pdf);
        using var response = await client.PostAsync("/api/documents", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new(body.GetProperty("documentId").GetGuid(), body.GetProperty("versionId").GetGuid(), body.GetProperty("ingestionRunId").GetGuid());
    }

    /// <summary>The progress rows of one stage, checked to sit between that stage's Started and Completed rows.</summary>
    private static async Task<List<StageRow>> ProgressOfAsync(List<StageRow> rows, string stage)
    {
        var started = rows.FindIndex(r => r.Stage == stage && r.Status == "Started");
        var completed = rows.FindIndex(r => r.Stage == stage && r.Status == "Completed");
        await Assert.That(started).IsGreaterThanOrEqualTo(0);
        await Assert.That(completed).IsGreaterThan(started);
        var progress = rows.Where(r => r.Stage == stage && r.Status == "Progress").ToList();
        await Assert.That(progress.All(p => rows.IndexOf(p) > started && rows.IndexOf(p) < completed)).IsTrue();
        return progress;
    }

    [Test]
    public async Task Progress_fiftyPagePdf_reportsExtractionPagesAndEmbeddedPassages()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: SmallPassages);

        var doc = await ImportPdfAsync(app, Create(Enumerable.Range(1, 50).Select(TextPage)));
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var rows = await StageRowsAsync(app, doc.RunId);
        var passages = await app.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId);
        await Assert.That(passages).IsGreaterThan(200);

        var extracting = await ProgressOfAsync(rows, "Extracting");
        await Assert.That(extracting.Count).IsBetween(1, 9);
        await Assert.That(extracting.All(p => p.Total == 50)).IsTrue();
        await Assert.That(extracting.Select(p => p.Current!.Value).ToArray()).IsEquivalentTo(extracting.Select(p => p.Current!.Value).Order().Distinct().ToArray());
        await Assert.That(extracting.All(p => p.Current < 50)).IsTrue();

        var embedding = await ProgressOfAsync(rows, "Embedding");
        await Assert.That(embedding.Count).IsBetween(1, 9);
        await Assert.That(embedding.All(p => p.Total == passages)).IsTrue();
        await Assert.That(embedding.Select(p => p.Current!.Value).ToArray()).IsEquivalentTo(embedding.Select(p => p.Current!.Value).Order().Distinct().ToArray());
        await Assert.That(embedding.All(p => p.Current < passages && p.Current % 16 == 0)).IsTrue();
    }

    [Test]
    public async Task Progress_smallDocumentOfThreeBatches_hasTwoEmbeddingEventsAndNoExtractionProgress()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var markdown = new StringBuilder($"# Policy {Guid.NewGuid()}\n\n");
        for (var i = 0; i < 40; i++)
        {
            markdown.Append(System.Globalization.CultureInfo.InvariantCulture, $"## Section {i}\n\nBody text of section {i} is long enough to index.\n\n");
        }

        var doc = await app.ImportAsync(markdown.ToString());
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var rows = await StageRowsAsync(app, doc.RunId);
        var embedding = await ProgressOfAsync(rows, "Embedding");
        await Assert.That(embedding.Select(p => (p.Current, p.Total)).ToArray()).IsEquivalentTo([((int?)16, (int?)40), (32, 40)]);
        await Assert.That(rows.Any(r => r.Stage == "Extracting" && r.Status == "Progress")).IsFalse();
        await Assert.That(rows.Where(r => r.Status != "Progress").All(r => r.Current is null && r.Total is null)).IsTrue();
    }

    [Test]
    public async Task Progress_singleBatch_hasNoProgressEvents()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await app.ImportAsync();

        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That((await StageRowsAsync(app, doc.RunId)).Any(r => r.Status == "Progress")).IsFalse();
    }

    [Test]
    public async Task Stream_progressEvents_carryCurrentAndTotalAndOthersCarryNull()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: SmallPassages);
        var doc = await ImportPdfAsync(app, Create(Enumerable.Range(1, 20).Select(TextPage)));
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        using var client = app.CreateClientAs(Persona.ContentOwner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var response = await client.GetAsync($"/api/documents/{doc.DocumentId}/ingestion/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var items = new List<JsonElement>();
        var eventIds = new List<long>();
        await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
        {
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
            {
                if (item.EventType == "ingestion.stage")
                {
                    items.Add(JsonDocument.Parse(item.Data).RootElement.Clone());
                    eventIds.Add(long.Parse(item.EventId!, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var progress = items.Where(i => i.GetProperty("status").GetString() == "progress").ToList();
        await Assert.That(progress.Count).IsGreaterThan(0);
        await Assert.That(progress.All(p => p.GetProperty("current").GetInt32() < p.GetProperty("total").GetInt32())).IsTrue();
        await Assert.That(progress.Any(p => p.GetProperty("stage").GetString() == "extracting" && p.GetProperty("total").GetInt32() == 20)).IsTrue();
        await Assert.That(progress.Any(p => p.GetProperty("stage").GetString() == "embedding")).IsTrue();
        var others = items.Where(i => i.GetProperty("status").GetString() != "progress").ToList();
        await Assert.That(others.All(o => o.GetProperty("current").ValueKind == JsonValueKind.Null && o.GetProperty("total").ValueKind == JsonValueKind.Null)).IsTrue();
        await Assert.That(items[^1].GetProperty("stage").GetString()).IsEqualTo("completed");
        await Assert.That(eventIds.Order().ToArray()).IsEquivalentTo(eventIds.ToArray());
    }

    [Test]
    public async Task Stream_resumeAfterAProgressEvent_continuesFromTheNextEvent()
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: SmallPassages);
        var doc = await ImportPdfAsync(app, Create(Enumerable.Range(1, 20).Select(TextPage)));
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        var progressId = await app.ScalarAsync<long>(
            "SELECT min(id) FROM documents.ingestion_events WHERE run_id = $1 AND status = 'Progress'", doc.RunId);
        using var client = app.CreateClientAs(Persona.ContentOwner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{doc.DocumentId}/ingestion/events");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", progressId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var ids = new List<long>();
        await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
        {
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
            {
                if (item.EventType == "ingestion.stage")
                {
                    ids.Add(long.Parse(item.EventId!, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        await Assert.That(ids.Count).IsGreaterThan(0);
        await Assert.That(ids.All(id => id > progressId)).IsTrue();
    }

    [Test]
    public async Task Metrics_pdfImportExtractionAndRejections_recordTheExpectedValues()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
        var longs = new ConcurrentBag<(string Name, long Value, string Tag)>();
        var doubles = new ConcurrentBag<(string Name, double Value, string Tag)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "RagAssistant.Documents" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => longs.Add((i.Name, v, tags.Length > 0 ? tags[0].Value?.ToString() ?? string.Empty : string.Empty)));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => doubles.Add((i.Name, v, tags.Length > 0 ? tags[0].Value?.ToString() ?? string.Empty : string.Empty)));
        listener.Start();
        var pdf = Create(Page(Body(700, "First page text that is long enough to count.")), Page(Body(700, $"Second page {Guid.NewGuid()} text that is long enough.")));

        var doc = await ImportPdfAsync(app, pdf);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        using var client = app.CreateClientAs(Persona.ContentOwner);
        using var duplicate = TestPdfs.Form(pdf);
        using var rejected = await client.PostAsync("/api/documents", duplicate);

        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(longs.Where(m => m.Name == "documents.imported").Select(m => m.Tag).ToArray()).IsEquivalentTo(["pdf"]);
        await Assert.That(longs.Where(m => m.Name == "documents.import.rejected").Select(m => m.Tag).ToArray()).IsEquivalentTo(["documents.duplicate_content"]);
        await Assert.That(longs.Where(m => m.Name == "documents.pdf.pages.extracted").Sum(m => m.Value)).IsEqualTo(2L);
        var durations = doubles.Where(m => m.Name == "documents.pdf.extraction.duration").ToArray();
        await Assert.That(durations.Length).IsEqualTo(1);
        await Assert.That(durations[0].Tag).IsEqualTo("completed");
        await Assert.That(durations[0].Value).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task Metrics_blobMissing_countsAFailedIngestionAndNoExtraction()
    {
        await using var app = await LifecycleHelpers.StartWithoutEmbeddingsAsync(postgres);
        var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
        var longs = new ConcurrentBag<(string Name, long Value, string Tag)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "RagAssistant.Documents" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => longs.Add((i.Name, v, tags.Length > 0 ? tags[0].Value?.ToString() ?? string.Empty : string.Empty)));
        listener.Start();
        var doc = await ImportPdfAsync(app, Create(Page(Body(700, "Text of a page whose file is about to vanish."))));
        var key = await app.ScalarAsync<string>("SELECT storage_key FROM documents.document_versions WHERE id = $1", doc.VersionId);
        File.Delete(Path.Combine(app.StoragePath, key!.Replace('/', Path.DirectorySeparatorChar)));

        app.Generator.Available = true;
        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(longs.Where(m => m.Name == "documents.ingestion.failed").Select(m => m.Tag).ToArray()).IsEquivalentTo(["documents.file_missing"]);
        await Assert.That(longs.Any(m => m.Name == "documents.pdf.pages.extracted")).IsFalse();
    }
}
