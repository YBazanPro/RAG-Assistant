using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RagAssistant.Integration.Tests.Chat;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Evaluation;

// Spec 020, AC-20 to AC-24: harness reports uploaded to, listed, compared and deleted from the Evaluation module.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class EvaluationRunsTests(RagAssistantAppFactory factory)
{
    /// <summary>A report shaped like the harness's <c>report.json</c> (schema 1), with only what the module reads plus a case.</summary>
    public static JsonObject Report(string runId, string chatModel = "qwen3:1.7b", double recallAt5 = 0.8, string scope = "sample", string dataset = "d1") => new()
    {
        ["schemaVersion"] = 1,
        ["runId"] = runId,
        ["startedAt"] = "2026-10-20T02:00:00+00:00",
        ["finishedAt"] = "2026-10-20T02:14:00+00:00",
        ["valid"] = true,
        ["invalidReason"] = null,
        ["answerMetricsMeasured"] = true,
        ["configuration"] = new JsonObject
        {
            ["chatProvider"] = "Ollama",
            ["chatModel"] = chatModel,
            ["embeddingModel"] = "bge-m3",
            ["topK"] = 8,
            ["corpusHash"] = "c1",
            ["datasetHash"] = dataset,
            ["scope"] = scope,
            ["gitCommit"] = "abc",
            ["retrievalMode"] = "hybrid",
            ["reranking"] = false,
        },
        ["brokenCases"] = new JsonArray(),
        ["overall"] = new JsonObject { ["cases"] = 2, ["errors"] = 0, ["recallAt5"] = recallAt5, ["mrr"] = 0.7 },
        ["byCategory"] = new JsonObject { ["policy"] = new JsonObject { ["cases"] = 2, ["recallAt5"] = recallAt5 } },
        ["byQuestionLanguage"] = new JsonObject { ["fr"] = new JsonObject { ["cases"] = 1, ["recallAt5"] = recallAt5 } },
        ["cases"] = new JsonArray(new JsonObject { ["id"] = "p-en-01", ["category"] = "policy", ["status"] = "complete" }),
    };

    private static string NewRunId() => $"test-{Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, JsonNode report) =>
        await client.PostAsync("/api/admin/evaluation/runs", new StringContent(report.ToJsonString(), Encoding.UTF8, "application/json"));

    // AC-20
    [Test]
    public async Task Upload_createsThenReplaces_keepingOneRun()
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);
        var id = NewRunId();

        using var first = await UploadAsync(client, Report(id, recallAt5: 0.5));
        using var second = await UploadAsync(client, Report(id, recallAt5: 0.9));
        var (_, list) = await client.GetJsonAsync("/api/admin/evaluation/runs?pageSize=100&chatModel=qwen3%3A1.7b");
        var (detailStatus, detail) = await client.GetJsonAsync($"/api/admin/evaluation/runs/{id}");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var rows = list.GetProperty("items").EnumerateArray().Where(r => r.GetProperty("id").GetString() == id).ToList();
        await Assert.That(rows.Count).IsEqualTo(1);
        // AC-25's data: the row has headline metrics, the detail metrics per category and language.
        await Assert.That(rows[0].GetProperty("headline").GetProperty("recallAt5").GetDouble()).IsEqualTo(0.9);
        await Assert.That(rows[0].GetProperty("retrievalMode").GetString()).IsEqualTo("hybrid");
        await Assert.That(detailStatus).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(detail.GetProperty("metrics").GetProperty("byCategory").GetProperty("policy").GetProperty("recallAt5").GetDouble()).IsEqualTo(0.9);
        await Assert.That(detail.GetProperty("metrics").GetProperty("byQuestionLanguage").GetProperty("fr").GetProperty("cases").GetInt32()).IsEqualTo(1);
        await Assert.That(detail.GetProperty("cases").GetArrayLength()).IsEqualTo(1);
        await Assert.That(detail.GetProperty("judge").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task JudgedUpload_setsTheJudge_andALaterPlainUploadKeepsIt()
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);
        var id = NewRunId();
        var judged = Report(id);
        judged["judge"] = new JsonObject { ["judgeModel"] = "gemma4:12b", ["overall"] = new JsonObject { ["answers"] = 2, ["groundedness"] = 4.5 } };

        using var withJudge = await UploadAsync(client, judged);
        using var plain = await UploadAsync(client, Report(id));
        var (_, detail) = await client.GetJsonAsync($"/api/admin/evaluation/runs/{id}");

        await Assert.That(withJudge.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(detail.GetProperty("run").GetProperty("judgeModel").GetString()).IsEqualTo("gemma4:12b");
        await Assert.That(detail.GetProperty("run").GetProperty("headline").GetProperty("groundedness").GetDouble()).IsEqualTo(4.5);
    }

    // AC-23 and an unknown schema version
    [Test]
    public async Task Upload_ofAnotherScopeOrSchema_orNotJson_isAValidationError()
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);
        var wrongSchema = Report(NewRunId());
        wrongSchema["schemaVersion"] = 99;

        using var otherScope = await UploadAsync(client, Report(NewRunId(), scope: "hr"));
        using var schema = await UploadAsync(client, wrongSchema);
        using var garbage = await client.PostAsync("/api/admin/evaluation/runs", new StringContent("{not json", Encoding.UTF8, "application/json"));

        foreach (var (response, field) in new[] { (otherScope, "configuration.scope"), (schema, "schemaVersion"), (garbage, "report") })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("validation.failed");
            await Assert.That(problem.GetProperty("errors").TryGetProperty(field, out _)).IsTrue();
        }
    }

    [Test]
    public async Task Upload_overTenMebibytes_is413()
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);
        var big = Report(NewRunId());
        big["padding"] = new string('x', 10 * 1024 * 1024);

        using var response = await UploadAsync(client, big);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

    // AC-22
    [Test]
    public async Task Compare_returnsDeltasAndFlagsDifferences()
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);
        var a = NewRunId();
        var b = NewRunId();
        using (await UploadAsync(client, Report(a, chatModel: "qwen3:1.7b", recallAt5: 0.6)))
        using (await UploadAsync(client, Report(b, chatModel: "ministral-3:8b", recallAt5: 0.85, dataset: "d2")))
        {
        }

        var (status, comparison) = await client.GetJsonAsync($"/api/admin/evaluation/compare?a={a}&b={b}");
        var (missing, _) = await client.GetJsonAsync($"/api/admin/evaluation/compare?a={a}&b=nope");
        var (invalid, _) = await client.GetJsonAsync($"/api/admin/evaluation/compare?a={a}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(comparison.GetProperty("datasetDiffers").GetBoolean()).IsTrue();
        string[] keys = [.. comparison.GetProperty("configurationDifferences").EnumerateArray().Select(d => d.GetProperty("key").GetString()!)];
        await Assert.That(keys).IsEquivalentTo(["chatModel", "datasetHash"]);
        var overall = comparison.GetProperty("groups").EnumerateArray().First(g => g.GetProperty("name").GetString() == "overall");
        var recall = overall.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "recallAt5");
        await Assert.That(recall.GetProperty("delta").GetDouble()).IsEqualTo(0.25);
        await Assert.That(comparison.GetProperty("groups").EnumerateArray().Any(g => g.GetProperty("name").GetString() == "language:fr")).IsTrue();
        await Assert.That(missing).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(invalid).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // AC-24
    [Test]
    public async Task Delete_removesTheRun()
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);
        var id = NewRunId();
        using (await UploadAsync(client, Report(id)))
        {
        }

        using var deleted = await client.DeleteAsync($"/api/admin/evaluation/runs/{id}");
        using var again = await client.DeleteAsync($"/api/admin/evaluation/runs/{id}");
        var (get, _) = await client.GetJsonAsync($"/api/admin/evaluation/runs/{id}");

        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(get).IsEqualTo(HttpStatusCode.NotFound);
    }

    public static IEnumerable<Func<(HttpMethod Method, string Url)>> Routes()
    {
        yield return () => (HttpMethod.Post, "/api/admin/evaluation/runs");
        yield return () => (HttpMethod.Get, "/api/admin/evaluation/runs");
        yield return () => (HttpMethod.Get, "/api/admin/evaluation/runs/x");
        yield return () => (HttpMethod.Get, "/api/admin/evaluation/compare?a=x&b=y");
        yield return () => (HttpMethod.Delete, "/api/admin/evaluation/runs/x");
    }

    // AC-21
    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Route_isForMaintainersOnly((HttpMethod Method, string Url) route)
    {
        using var anonymous = factory.CreateClientAs(Persona.Anonymous);
        using var anonymousResponse = await anonymous.SendAsync(new HttpRequestMessage(route.Method, route.Url) { Content = Body(route.Method) });
        await Assert.That(anonymousResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        foreach (var persona in new[] { Persona.KnowledgeSeeker, Persona.ContentOwner })
        {
            using var client = factory.CreateClientAs(persona);
            using var response = await client.SendAsync(new HttpRequestMessage(route.Method, route.Url) { Content = Body(route.Method) });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    private static StringContent? Body(HttpMethod method) =>
        method == HttpMethod.Post ? new StringContent(Report(NewRunId()).ToJsonString(), Encoding.UTF8, "application/json") : null;
}
