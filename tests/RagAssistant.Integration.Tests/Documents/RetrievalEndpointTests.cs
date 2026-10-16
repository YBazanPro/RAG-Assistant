using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 011, endpoint: AC-5, AC-10, AC-13, AC-15, AC-19, AC-21 to AC-23, AC-25.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class RetrievalEndpointTests(PostgresFixture postgres)
{
    private const string SearchUrl = "/api/documents/search?q=budget%20exception";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(RetrievalBed bed, string url, Persona persona = Persona.Maintainer)
    {
        using var client = bed.App.CreateClientAs(persona);
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var body = response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body);
    }

    [Test]
    [Arguments(Persona.Anonymous, HttpStatusCode.Unauthorized)]
    [Arguments(Persona.KnowledgeSeeker, HttpStatusCode.Forbidden)]
    [Arguments(Persona.ContentOwner, HttpStatusCode.Forbidden)]
    [Arguments(Persona.Maintainer, HttpStatusCode.OK)]
    public async Task Search_perPersona_onlyMaintainersMayCall(Persona persona, HttpStatusCode expected)
    {
        await using var bed = await StartAsync(postgres);
        using var client = bed.App.CreateClientAs(persona);

        var response = await client.GetAsync(new Uri(SearchUrl, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(expected);
    }

    [Test]
    public async Task Search_asMaintainer_returnsDiagnosticsShape()
    {
        await using var bed = await StartAsync(postgres);
        const string same = "Budget exceptions above ten thousand dollars need approval from the finance director.";
        var (doc, ids) = await bed.AddAsync(
            new SeedDocument("Budget Exception Policy", "finance"),
            P(0.912345, same, otherAxis: 1, page: 4, section: ["Approval"]),
            P(0.9, same, otherAxis: 2),
            P(0.4, otherAxis: 3));

        var (status, body) = await GetAsync(bed, SearchUrl + "&topK=5");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("query").GetString()).IsEqualTo("budget exception");
        await Assert.That(body.GetProperty("outcome").GetString()).IsEqualTo("sufficient");
        await Assert.That(body.GetProperty("topK").GetInt32()).IsEqualTo(5);
        await Assert.That(body.GetProperty("embeddingModel").GetString()).IsEqualTo(Model);
        await Assert.That(body.GetProperty("thresholds").GetProperty("minScore").GetDouble()).IsEqualTo(0.5);
        await Assert.That(body.GetProperty("thresholds").GetProperty("confidentScore").GetDouble()).IsEqualTo(0.7);
        await Assert.That(body.GetProperty("thresholds").GetProperty("duplicateOverlap").GetDouble()).IsEqualTo(0.8);
        await Assert.That(body.GetProperty("skippedForModelMismatch").GetInt32()).IsEqualTo(0);

        var passage = body.GetProperty("passages").EnumerateArray().Single();
        await Assert.That(passage.GetProperty("rank").GetInt32()).IsEqualTo(1);
        await Assert.That(passage.GetProperty("passageId").GetGuid()).IsEqualTo(ids[0]);
        await Assert.That(passage.GetProperty("documentId").GetGuid()).IsEqualTo(doc.DocumentId);
        await Assert.That(passage.GetProperty("documentTitle").GetString()).IsEqualTo("Budget Exception Policy");
        await Assert.That(passage.GetProperty("versionNumber").GetInt32()).IsEqualTo(1);
        await Assert.That(passage.GetProperty("pageStart").GetInt32()).IsEqualTo(4);
        await Assert.That(passage.GetProperty("text").GetString()).IsEqualTo(same);
        await Assert.That(passage.GetProperty("score").GetDouble()).IsEqualTo(0.9123);

        var discarded = body.GetProperty("discarded").EnumerateArray().ToArray();
        await Assert.That(discarded.Length).IsEqualTo(2);
        var duplicate = discarded.Single(d => d.GetProperty("reason").GetString() == "duplicate");
        await Assert.That(duplicate.GetProperty("passageId").GetGuid()).IsEqualTo(ids[1]);
        await Assert.That(duplicate.GetProperty("duplicateOf").GetGuid()).IsEqualTo(ids[0]);
        await Assert.That(duplicate.GetProperty("overlap").GetDouble()).IsEqualTo(1.0);
        await Assert.That(duplicate.GetProperty("score").GetDouble()).IsEqualTo(0.9);
        var low = discarded.Single(d => d.GetProperty("reason").GetString() == "below_min_score");
        await Assert.That(low.GetProperty("passageId").GetGuid()).IsEqualTo(ids[2]);
        await Assert.That(low.GetProperty("score").GetDouble()).IsEqualTo(0.4);
        await Assert.That(discarded.Any(d => d.TryGetProperty("text", out _))).IsFalse();
    }

    [Test]
    public async Task Search_otherEmbeddingModel_isCountedAsSkipped()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync(new SeedDocument("Old model", "owner", EmbeddingModel: "nomic-embed-text"), P(0.95));
        await bed.AddAsync(new SeedDocument("Old model, other language", "owner", Language: "de", EmbeddingModel: "nomic-embed-text"), P(0.95));
        await bed.AddAsync("Current model", P(0.6));

        var (_, all) = await GetAsync(bed, SearchUrl);
        var (_, english) = await GetAsync(bed, SearchUrl + "&language=en");

        await Assert.That(all.GetProperty("skippedForModelMismatch").GetInt32()).IsEqualTo(2);
        await Assert.That(english.GetProperty("skippedForModelMismatch").GetInt32()).IsEqualTo(1);
        await Assert.That(english.GetProperty("passages").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Search_emptyCorpus_isNoEvidence()
    {
        await using var bed = await StartAsync(postgres);

        var (status, body) = await GetAsync(bed, SearchUrl);

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("outcome").GetString()).IsEqualTo("noEvidence");
        await Assert.That(body.GetProperty("passages").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments("/api/documents/search", "q")]
    [Arguments("/api/documents/search?q=%20%20", "q")]
    [Arguments("/api/documents/search?q=budget&topK=0", "topK")]
    [Arguments("/api/documents/search?q=budget&topK=21", "topK")]
    [Arguments("/api/documents/search?q=budget&topK=abc", "topK")]
    [Arguments("/api/documents/search?q=budget&language=eng", "language")]
    public async Task Search_invalidParameters_return400WithFieldError(string url, string field)
    {
        await using var bed = await StartAsync(postgres);

        var (status, body) = await GetAsync(bed, url);

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(body.GetProperty("errors").TryGetProperty(field, out _)).IsTrue();
    }

    [Test]
    public async Task Search_tooLongQueryOrTooManyTags_return400()
    {
        await using var bed = await StartAsync(postgres);
        var tags = string.Concat(Enumerable.Range(0, 21).Select(i => $"&tags=t{i}"));

        var (longStatus, longBody) = await GetAsync(bed, "/api/documents/search?q=" + new string('a', 1001));
        var (tagsStatus, tagsBody) = await GetAsync(bed, SearchUrl + tags);

        await Assert.That(longStatus).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(longBody.GetProperty("errors").TryGetProperty("q", out _)).IsTrue();
        await Assert.That(tagsStatus).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tagsBody.GetProperty("errors").TryGetProperty("tags", out _)).IsTrue();
    }

    [Test]
    public async Task Search_modelUnavailable_returns503()
    {
        await using var bed = await StartAsync(postgres);
        bed.Generator.AlwaysFail = true;

        var (status, body) = await GetAsync(bed, SearchUrl);

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("retrieval.embedding_unavailable");
    }

    [Test]
    public async Task Search_topKOmitted_usesDefaultTopK()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync("Many", [.. Enumerable.Range(0, 12).Select(i => P(0.9 - (i * 0.01), otherAxis: 1 + i))]);

        var (_, body) = await GetAsync(bed, SearchUrl);

        await Assert.That(body.GetProperty("topK").GetInt32()).IsEqualTo(8);
        await Assert.That(body.GetProperty("passages").GetArrayLength()).IsEqualTo(8);
    }

    [Test]
    public async Task Search_routeDoesNotClashWithDocumentById()
    {
        await using var bed = await StartAsync(postgres);
        var (doc, _) = await bed.AddAsync("Doc", P(0.9));

        var (searchStatus, search) = await GetAsync(bed, SearchUrl);
        var (getStatus, get) = await GetAsync(bed, $"/api/documents/{doc.DocumentId}");

        await Assert.That(searchStatus).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(search.TryGetProperty("passages", out _)).IsTrue();
        await Assert.That(getStatus).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(get.GetProperty("id").GetGuid()).IsEqualTo(doc.DocumentId);
    }

    [Test]
    public async Task Search_telemetry_recordsSpanAndOutcomeAndKeepsQueryAtDebug()
    {
        await using var bed = await StartAsync(postgres, new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Debug" });
        await bed.AddAsync("Doc", P(0.9));
        var meterFactory = bed.App.Services.GetRequiredService<IMeterFactory>();
        var outcomes = new ConcurrentBag<string?>();
        var stopped = new ConcurrentBag<Activity>();

        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "RagAssistant.Documents" && ReferenceEquals(instrument.Meter.Scope, meterFactory)
                    && instrument.Name == "documents.search.outcomes")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcomes.Add(tag.Value as string);
                }
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

        const string secret = "budget question nobody else asks 7f3a";
        // The listener sees every search in the process, including other tests' running in parallel: pick this request's span by
        // its trace, which the API continues from the traceparent header.
        var traceId = ActivityTraceId.CreateRandom();
        using var client = bed.App.CreateClientAs(Persona.Maintainer);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/documents/search?q=" + Uri.EscapeDataString(secret), UriKind.Relative));
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var span = stopped.Single(a => a.OperationName == "documents.search" && a.TraceId == traceId);
        await Assert.That(span.GetTagItem("documents.search.outcome")).IsEqualTo("sufficient");
        await Assert.That(span.GetTagItem("documents.search.candidates")).IsEqualTo(1);
        await Assert.That(span.GetTagItem("documents.search.embedding.cache_hit") is false).IsTrue();
        await Assert.That(outcomes.Contains("sufficient")).IsTrue();

        var mentions = bed.App.Logs.Entries.Where(e => e.Message.Contains(secret, StringComparison.Ordinal)).ToArray();
        await Assert.That(mentions.Length).IsGreaterThan(0);
        await Assert.That(mentions.All(e => e.Level <= LogLevel.Debug)).IsTrue();
    }
}
