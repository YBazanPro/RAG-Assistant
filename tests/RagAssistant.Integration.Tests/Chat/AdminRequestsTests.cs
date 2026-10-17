using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Documents;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 017, AC-3 to AC-11: maintainers read the traces of all users, never answer or passage text, never a private title.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class AdminRequestsTests(PostgresFixture postgres)
{
    private const string PassageText = "UNIQUE-PASSAGE-TEXT-4711";

    private static async Task<(HttpStatusCode Status, string Raw, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, raw, raw.Length == 0 ? default : JsonDocument.Parse(raw).RootElement.Clone());
    }

    private static HttpClient Maintainer(ChatBed bed) => bed.App.CreateClientAs(Persona.Maintainer);

    // AC-3
    [Test]
    public async Task List_showsTheRequestsOfAllUsersNewestFirst_withoutAnswerOrPassageText()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9, PassageText));
        var (first, firstId) = await bed.NewConversationAsync("user-a");
        var (second, secondId) = await bed.NewConversationAsync("user-b");
        var longQuestion = "budget " + new string('x', 200);
        await ChatBed.AskAsync(first, firstId);
        await ChatBed.AskAsync(second, secondId, longQuestion);
        using var admin = Maintainer(bed);

        var (status, raw, body) = await GetAsync(admin, "/api/admin/chat/requests");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(items.Count).IsEqualTo(2);
        await Assert.That(items[0].GetProperty("ownerSub").GetString()).IsEqualTo("user-b");
        await Assert.That(items[1].GetProperty("ownerSub").GetString()).IsEqualTo("user-a");
        await Assert.That(items[0].GetProperty("questionText").GetString()!.Length).IsEqualTo(121);
        await Assert.That(items[0].GetProperty("questionText").GetString()!.EndsWith('…')).IsTrue();
        await Assert.That(body.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
        await Assert.That(raw).DoesNotContain("Department head");
        await Assert.That(raw).DoesNotContain(PassageText);
    }

    // AC-4
    [Test]
    public async Task List_filtersByStatusAndByNegativeFeedback_andRejectsUnknownValues()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var complete = await ChatBed.AskAsync(client, id);
        await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);
        using var admin = Maintainer(bed);

        var refused = await GetAsync(admin, "/api/admin/chat/requests?status=refused");
        var refusedOrFailed = await GetAsync(admin, "/api/admin/chat/requests?status=refused&status=failed");
        var negativeBefore = await GetAsync(admin, "/api/admin/chat/requests?feedback=negative");
        using var rated = await client.PutAsJsonAsync(
            $"/api/chat/conversations/{id}/messages/{complete.AssistantMessageId}/rating", new { rating = "not_helpful" });
        var negativeAfter = await GetAsync(admin, "/api/admin/chat/requests?feedback=negative");
        var bogus = await GetAsync(admin, "/api/admin/chat/requests?status=bogus");
        var pending = await GetAsync(admin, "/api/admin/chat/requests?status=pending");
        var badFeedback = await GetAsync(admin, "/api/admin/chat/requests?feedback=positive");
        var hugePage = await GetAsync(admin, "/api/admin/chat/requests?pageSize=101");

        await Assert.That(refused.Body.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
        await Assert.That(refused.Body.GetProperty("items")[0].GetProperty("finalStatus").GetString()).IsEqualTo("refused");
        await Assert.That(refusedOrFailed.Body.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
        await Assert.That(negativeBefore.Body.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
        await Assert.That(negativeAfter.Body.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
        await Assert.That(negativeAfter.Body.GetProperty("items")[0].GetProperty("hasNegativeFeedback").GetBoolean()).IsTrue();
        await Assert.That(bogus.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(bogus.Body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(pending.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(badFeedback.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(hugePage.Status).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // AC-5
    [Test]
    public async Task Detail_ofAFollowUp_showsTheRewriteRetrievalGenerationAndResult_withoutTexts()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9, PassageText));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        bed.Chat.ScriptRewrite("Who approves a budget exception above ten thousand dollars?");
        var followUp = await ChatBed.AskAsync(client, id, "And above that amount?");
        using var admin = Maintainer(bed);
        var traceId = await bed.App.ScalarAsync<Guid>(
            "SELECT id FROM chat.request_traces WHERE assistant_message_id = $1", followUp.AssistantMessageId);

        var (status, raw, body) = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("question").GetProperty("text").GetString()).IsEqualTo("And above that amount?");
        var rewrite = body.GetProperty("rewrite");
        await Assert.That(rewrite.GetProperty("used").GetBoolean()).IsTrue();
        await Assert.That(rewrite.GetProperty("retrievalQuery").GetString()).IsEqualTo("Who approves a budget exception above ten thousand dollars?");
        var retrieval = body.GetProperty("retrieval");
        await Assert.That(retrieval.GetProperty("evidenceOutcome").GetString()).IsEqualTo("Sufficient");
        await Assert.That(retrieval.GetProperty("minScore").GetDouble()).IsEqualTo(0.5);
        await Assert.That(retrieval.GetProperty("confidentScore").GetDouble()).IsEqualTo(0.7);
        await Assert.That(retrieval.GetProperty("passages").GetArrayLength()).IsEqualTo(1);
        await Assert.That(retrieval.GetProperty("passages")[0].GetProperty("cited").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("generation").GetProperty("chatModel").GetString()).IsEqualTo(ScriptedChatClient.ModelId);
        await Assert.That(body.GetProperty("generation").GetProperty("promptVersion").GetString()).IsNotNull();
        await Assert.That(body.GetProperty("result").GetProperty("finalStatus").GetString()).IsEqualTo("complete");
        await Assert.That(raw).DoesNotContain("Department head");
        await Assert.That(raw).DoesNotContain(PassageText);
    }

    // AC-6 and AC-7
    [Test]
    public async Task Detail_namesOnlyDocumentsThatAreCurrentlyShared_whateverTheTraceStored()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        const string Owner = "private-owner";
        var (secret, _) = await bed.AddAsync(new SeedDocument("Secret salaries", "owner", Visibility: "Private", CreatedBy: Owner), P(0.95));
        var (shared, _) = await bed.AddAsync("Public handbook", P(0.8));
        var client = bed.ClientFor(Owner);
        var id = await client.CreateConversationAsync();
        var answer = await ChatBed.AskAsync(client, id);
        using var admin = Maintainer(bed);
        var traceId = await bed.App.ScalarAsync<Guid>(
            "SELECT id FROM chat.request_traces WHERE assistant_message_id = $1", answer.AssistantMessageId);
        var stored = await bed.App.ScalarAsync<string>("SELECT retrieved_passages::text FROM chat.request_traces WHERE id = $1", traceId);

        var (_, raw, body) = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");

        // The guard matters only because the trace itself does hold the private title.
        await Assert.That(stored).Contains("Secret salaries");
        await Assert.That(raw).DoesNotContain("Secret salaries");
        var titles = body.GetProperty("retrieval").GetProperty("passages").EnumerateArray()
            .ToDictionary(p => p.GetProperty("documentId").GetGuid(), p => p.GetProperty("documentTitle").GetString());
        await Assert.That(titles[secret.DocumentId]).IsNull();
        await Assert.That(titles[shared.DocumentId]).IsEqualTo("Public handbook");

        // A document that turns private after the answer is hidden from then on.
        await bed.App.ExecuteAsync("UPDATE documents.documents SET visibility = 'Private' WHERE id = $1", shared.DocumentId);
        var (_, afterRaw, _) = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");
        await Assert.That(afterRaw).DoesNotContain("Public handbook");

        // A deleted document is hidden as well.
        await bed.App.ExecuteAsync("UPDATE documents.documents SET visibility = 'Shared' WHERE id = $1", shared.DocumentId);
        await bed.App.ExecuteAsync("UPDATE documents.documents SET active_version_id = NULL WHERE id = $1", shared.DocumentId);
        await bed.App.ExecuteAsync("DELETE FROM documents.passages WHERE version_id = $1", shared.VersionId);
        await bed.App.ExecuteAsync("DELETE FROM documents.document_versions WHERE document_id = $1", shared.DocumentId);
        await bed.App.ExecuteAsync("DELETE FROM documents.documents WHERE id = $1", shared.DocumentId);
        var (afterDelete, deletedRaw, _) = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");
        await Assert.That(afterDelete).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(deletedRaw).DoesNotContain("Public handbook");
    }

    // AC-8
    [Test]
    public async Task Feedback_isShownOnTheRequestAndListedOncePerRatingAndReport()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync("reporter");
        var answer = await ChatBed.AskAsync(client, id);
        var baseUrl = $"/api/chat/conversations/{id}/messages/{answer.AssistantMessageId}";
        using var _ = await client.PutAsJsonAsync($"{baseUrl}/rating", new { rating = "not_helpful" });
        using var r1 = await client.PostAsJsonAsync($"{baseUrl}/reports", new { category = "incorrect", comment = "Wrong threshold" });
        using var r2 = await client.PostAsJsonAsync($"{baseUrl}/reports", new { category = "outdated" });
        using var admin = Maintainer(bed);
        var traceId = await bed.App.ScalarAsync<Guid>(
            "SELECT id FROM chat.request_traces WHERE assistant_message_id = $1", answer.AssistantMessageId);

        var (_, _, detail) = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");
        var (status, _, list) = await GetAsync(admin, "/api/admin/chat/feedback");

        var feedback = detail.GetProperty("feedback");
        await Assert.That(feedback.GetProperty("rating").GetString()).IsEqualTo("not_helpful");
        await Assert.That(feedback.GetProperty("reports").GetArrayLength()).IsEqualTo(2);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var rows = list.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows.Count(r => r.GetProperty("kind").GetString() == "rating")).IsEqualTo(1);
        await Assert.That(rows.Count(r => r.GetProperty("kind").GetString() == "report")).IsEqualTo(2);
        await Assert.That(rows.All(r => r.GetProperty("requestId").GetGuid() == traceId)).IsTrue();
        await Assert.That(rows.All(r => r.GetProperty("ownerSub").GetString() == "reporter")).IsTrue();
        await Assert.That(rows.Any(r => r.GetProperty("comment").GetString() == "Wrong threshold")).IsTrue();
    }

    [Test]
    public async Task Feedback_aHelpfulRatingIsNotListed()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var answer = await ChatBed.AskAsync(client, id);
        using var _ = await client.PutAsJsonAsync(
            $"/api/chat/conversations/{id}/messages/{answer.AssistantMessageId}/rating", new { rating = "helpful" });
        using var admin = Maintainer(bed);

        var (_, _, list) = await GetAsync(admin, "/api/admin/chat/feedback");

        await Assert.That(list.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
    }

    // AC-9
    [Test]
    public async Task Detail_traceUrlIsTheTemplateWithTheTraceId_orNullWhenUnset()
    {
        await using var withTemplate = await ChatBed.StartAsync(
            postgres, new Dictionary<string, string> { ["Admin:TraceUrlTemplate"] = "https://traces.test/detail/{traceId}" });
        await using var without = await ChatBed.StartAsync(postgres);

        foreach (var (bed, expected) in new[] { (withTemplate, "https://traces.test/detail/0123456789abcdef0123456789abcdef"), (without, null) })
        {
            await bed.AddAsync("Policy", P(0.9));
            var (client, id) = await bed.NewConversationAsync();
            var answer = await ChatBed.AskAsync(client, id);
            await bed.App.ExecuteAsync(
                "UPDATE chat.request_traces SET otel_trace_id = '0123456789abcdef0123456789abcdef' WHERE assistant_message_id = $1", answer.AssistantMessageId);
            var traceId = await bed.App.ScalarAsync<Guid>("SELECT id FROM chat.request_traces WHERE assistant_message_id = $1", answer.AssistantMessageId);
            using var admin = Maintainer(bed);

            var (_, _, body) = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");

            await Assert.That(body.GetProperty("otelTraceId").GetString()).IsEqualTo("0123456789abcdef0123456789abcdef");
            await Assert.That(body.GetProperty("traceUrl").GetString()).IsEqualTo(expected);
        }
    }

    // AC-10
    [Test]
    public async Task Detail_unknownIdIs404_andADeletedConversationDisappearsFromTheList()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var answer = await ChatBed.AskAsync(client, id);
        using var admin = Maintainer(bed);
        var traceId = await bed.App.ScalarAsync<Guid>("SELECT id FROM chat.request_traces WHERE assistant_message_id = $1", answer.AssistantMessageId);

        var unknown = await GetAsync(admin, $"/api/admin/chat/requests/{Guid.NewGuid()}");
        using var deleted = await client.DeleteAsync($"/api/chat/conversations/{id}");
        var afterDelete = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");
        var list = await GetAsync(admin, "/api/admin/chat/requests");

        await Assert.That(unknown.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.Body.GetProperty("code").GetString()).IsEqualTo("chat.request_not_found");
        await Assert.That(afterDelete.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(list.Body.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
    }

    // AC-11
    [Test]
    public async Task Detail_ofATraceWithoutTheDocumentFields_loadsWithThemNull()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (document, passages) = await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var answer = await ChatBed.AskAsync(client, id);
        var legacy = $$"""[{"passageId":"{{passages[0]}}","versionId":"{{document.VersionId}}","score":0.9,"rank":1,"marker":1,"cited":true}]""";
        await bed.App.ExecuteAsync(
            "UPDATE chat.request_traces SET retrieved_passages = $1::jsonb, rewrite_fallback_reason = NULL WHERE assistant_message_id = $2", legacy, answer.AssistantMessageId);
        var traceId = await bed.App.ScalarAsync<Guid>("SELECT id FROM chat.request_traces WHERE assistant_message_id = $1", answer.AssistantMessageId);
        using var admin = Maintainer(bed);

        var (status, _, body) = await GetAsync(admin, $"/api/admin/chat/requests/{traceId}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var passage = body.GetProperty("retrieval").GetProperty("passages")[0];
        await Assert.That(passage.GetProperty("documentId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(passage.GetProperty("documentTitle").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(body.GetProperty("titlesUnavailable").GetBoolean()).IsFalse();
    }
}
