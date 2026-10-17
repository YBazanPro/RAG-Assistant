using System.Net;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 017, AC-12, AC-13 and the Chat half of AC-22: the summary comes from the stored traces; configuration holds no secrets.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class AdminSummaryTests(PostgresFixture postgres)
{
    private static async Task<(HttpStatusCode Status, string Raw, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, raw, raw.Length == 0 ? default : JsonDocument.Parse(raw).RootElement.Clone());
    }

    // AC-12
    [Test]
    public async Task Summary_countsOnlyTheWindow_andComputesRatesPercentilesAndErrors()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var complete = await ChatBed.AskAsync(client, id);
        var refused = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);
        bed.Generator.AlwaysFail = true;
        var failed = await ChatBed.AskAsync(client, id, "Another budget question?");

        await bed.App.ExecuteAsync(
            "UPDATE chat.request_traces SET first_token_ms = 40, total_ms = 100, rewrite_fallback_reason = 'Timeout' WHERE assistant_message_id = $1",
            complete.AssistantMessageId);
        await bed.App.ExecuteAsync("UPDATE chat.request_traces SET total_ms = 300 WHERE assistant_message_id = $1", refused.AssistantMessageId);
        await bed.App.ExecuteAsync(
            "UPDATE chat.request_traces SET total_ms = 999, started_at = now() - interval '2 days' WHERE assistant_message_id = $1",
            failed.AssistantMessageId);
        using var admin = bed.App.CreateClientAs(Persona.Maintainer);

        var (day, _, today) = await GetAsync(admin, "/api/admin/chat/summary?window=24h");
        var (_, _, week) = await GetAsync(admin, "/api/admin/chat/summary");

        await Assert.That(day).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(today.GetProperty("window").GetString()).IsEqualTo("24h");
        await Assert.That(today.GetProperty("requests").GetInt32()).IsEqualTo(2);
        await Assert.That(today.GetProperty("byStatus").GetProperty("complete").GetInt32()).IsEqualTo(1);
        await Assert.That(today.GetProperty("byStatus").GetProperty("refused").GetInt32()).IsEqualTo(1);
        await Assert.That(today.GetProperty("refusalRate").GetDouble()).IsEqualTo(0.5);
        await Assert.That(today.GetProperty("byEvidenceOutcome").GetProperty("Sufficient").GetInt32()).IsEqualTo(1);
        await Assert.That(today.GetProperty("byEvidenceOutcome").GetProperty("NoEvidence").GetInt32()).IsEqualTo(1);
        await Assert.That(today.GetProperty("totalMs").GetProperty("p50").GetDouble()).IsEqualTo(200);
        await Assert.That(today.GetProperty("totalMs").GetProperty("p95").GetDouble()).IsEqualTo(290);

        // One request with a first token: both percentiles are that value.
        await Assert.That(today.GetProperty("firstTokenMs").GetProperty("p50").GetDouble()).IsEqualTo(40);
        await Assert.That(today.GetProperty("firstTokenMs").GetProperty("p95").GetDouble()).IsEqualTo(40);

        // A rewrite fallback counts as a rewrite error although the answer is complete.
        var todayErrors = today.GetProperty("errors").EnumerateArray().ToList();
        await Assert.That(todayErrors.Count).IsEqualTo(1);
        await Assert.That(todayErrors[0].GetProperty("stage").GetString()).IsEqualTo("rewrite");
        await Assert.That(todayErrors[0].GetProperty("errorCode").GetString()).IsEqualTo("rewrite.timeout");

        await Assert.That(week.GetProperty("window").GetString()).IsEqualTo("7d");
        await Assert.That(week.GetProperty("requests").GetInt32()).IsEqualTo(3);
        var weekErrors = week.GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("stage").GetString(), e.GetProperty("errorCode").GetString(), e.GetProperty("count").GetInt32()))
            .ToList();
        await Assert.That(weekErrors.Contains(("retrieval", "retrieval.embedding_unavailable", 1))).IsTrue();
        await Assert.That(weekErrors.Contains(("rewrite", "rewrite.timeout", 1))).IsTrue();
    }

    // AC-13
    [Test]
    public async Task Summary_withoutRequestsInTheWindow_isZeroWithNullPercentiles_andRejectsAnUnknownWindow()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        using var admin = bed.App.CreateClientAs(Persona.Maintainer);

        var (status, _, body) = await GetAsync(admin, "/api/admin/chat/summary?window=30d");
        var invalid = await GetAsync(admin, "/api/admin/chat/summary?window=1y");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("requests").GetInt32()).IsEqualTo(0);
        await Assert.That(body.GetProperty("refusalRate").GetDouble()).IsEqualTo(0);
        await Assert.That(body.GetProperty("firstTokenMs").GetProperty("p50").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(body.GetProperty("totalMs").GetProperty("p95").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(body.GetProperty("errors").GetArrayLength()).IsEqualTo(0);
        await Assert.That(invalid.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(invalid.Body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
    }

    // AC-22 (Chat half)
    [Test]
    public async Task Configuration_showsTheEffectiveOptions_andNoSecret()
    {
        await using var bed = await ChatBed.StartAsync(
            postgres, new Dictionary<string, string> { ["Chat:Retrieval:TopK"] = "5", ["Admin:TraceUrlTemplate"] = "https://secret-host.test/{traceId}" });
        using var admin = bed.App.CreateClientAs(Persona.Maintainer);

        var (status, raw, body) = await GetAsync(admin, "/api/admin/chat/configuration");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("topK").GetInt32()).IsEqualTo(5);
        await Assert.That(body.GetProperty("chatModel").GetString()).IsEqualTo(ScriptedChatClient.ModelId);
        await Assert.That(body.GetProperty("traceLinkConfigured").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("maxQuestionLength").GetInt32()).IsEqualTo(1000);
        await Assert.That(raw).DoesNotContain("secret-host.test");
        await Assert.That(raw).DoesNotContain("Password");
        await Assert.That(raw).DoesNotContain("Host=");
    }
}
