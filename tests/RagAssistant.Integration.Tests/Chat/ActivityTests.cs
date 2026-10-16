using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 015, AC-8, AC-11 to AC-14: what the assistant did, from the stream and from the stored trace.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class ActivityTests(PostgresFixture postgres)
{
    private const string PassageText = "Exceptions need the department head.";

    private static readonly string[] ForbiddenFields = ["inputTokens", "outputTokens", "prompt", "chatModel", "embeddingModel", "traceId"];
    private static readonly string[] FinanceTag = ["finance"];
    private static readonly string[] HrTag = ["hr"];

    private static string ActivityUrl(Guid conversationId, Guid assistantMessageId) =>
        $"/api/chat/conversations/{conversationId}/messages/{assistantMessageId}/activity";

    // AC-12, AC-14
    [Test]
    public async Task Activity_ofAGroundedAnswer_listsEveryStepAndNoTextOrTokens()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (document, _) = await bed.AddAsync(new SeedDocument("Budget Policy", "owner", Tags: ["finance"]), P(0.9, PassageText, page: 3, section: ["Approval"]), P(0.6));
        bed.Chat.Script("The head approves [1].");
        var (client, id) = await bed.NewConversationAsync();
        await client.PutAsJsonAsync($"/api/chat/conversations/{id}/scope", new { documentIds = new[] { document.DocumentId }, tags = FinanceTag });
        var result = await ChatBed.AskAsync(client, id);

        var (status, activity) = await client.GetJsonAsync(ActivityUrl(id, result.AssistantMessageId));

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(activity.GetProperty("question").GetString()).IsEqualTo(ChatBed.Question);
        await Assert.That(activity.GetProperty("rewrite").GetProperty("used").GetBoolean()).IsFalse();
        await Assert.That(activity.GetProperty("rewrite").GetProperty("query").GetString()).IsEqualTo(ChatBed.Question);

        var scope = activity.GetProperty("scope");
        await Assert.That(scope.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).SequenceEqual(["finance"])).IsTrue();
        var scopeDocument = scope.GetProperty("documents").EnumerateArray().Single();
        await Assert.That(scopeDocument.GetProperty("id").GetGuid()).IsEqualTo(document.DocumentId);
        await Assert.That(scopeDocument.GetProperty("title").GetString()).IsEqualTo("Budget Policy");
        await Assert.That(scopeDocument.GetProperty("available").GetBoolean()).IsTrue();

        var retrieval = activity.GetProperty("retrieval");
        await Assert.That(retrieval.GetProperty("outcome").GetString()).IsEqualTo("sufficient");
        await Assert.That(retrieval.GetProperty("minScore").GetDouble()).IsEqualTo(0.5);
        await Assert.That(retrieval.GetProperty("confidentScore").GetDouble()).IsEqualTo(0.7);
        var passages = retrieval.GetProperty("passages").EnumerateArray().ToArray();
        await Assert.That(passages.Length).IsEqualTo(2);
        await Assert.That(passages[0].GetProperty("rank").GetInt32()).IsEqualTo(1);
        await Assert.That(Math.Round(passages[0].GetProperty("score").GetDouble(), 3)).IsEqualTo(0.9);
        await Assert.That(passages[0].GetProperty("cited").GetBoolean()).IsTrue();
        await Assert.That(passages[0].GetProperty("documentTitle").GetString()).IsEqualTo("Budget Policy");
        await Assert.That(passages[0].GetProperty("pageStart").GetInt32()).IsEqualTo(3);
        await Assert.That(passages[1].GetProperty("cited").GetBoolean()).IsFalse();

        var answer = activity.GetProperty("answer");
        await Assert.That(answer.GetProperty("modelCalled").GetBoolean()).IsTrue();
        await Assert.That(answer.GetProperty("status").GetString()).IsEqualTo("complete");

        // AC-14: no passage text, prompt, token counts, model names or trace id
        var json = activity.GetRawText();
        await Assert.That(json).DoesNotContain(PassageText);
        await Assert.That(json).DoesNotContain("The head approves");
        foreach (var forbidden in ForbiddenFields)
        {
            await Assert.That(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase)).IsFalse();
        }
    }

    // AC-13
    [Test]
    public async Task Activity_ofARefusal_saysTheModelWasNotCalled()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var result = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);

        var (_, activity) = await client.GetJsonAsync(ActivityUrl(id, result.AssistantMessageId));

        var answer = activity.GetProperty("answer");
        await Assert.That(answer.GetProperty("modelCalled").GetBoolean()).IsFalse();
        await Assert.That(answer.GetProperty("status").GetString()).IsEqualTo("refused");
        await Assert.That(answer.GetProperty("refusalReason").GetString()).IsEqualTo("no_evidence");
        await Assert.That(activity.GetProperty("retrieval").GetProperty("outcome").GetString()).IsEqualTo("noEvidence");
    }

    // AC-6, AC-8
    [Test]
    public async Task Activity_showsTheScopeTheAnswerUsed_andMarksUnavailableDocuments()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (live, _) = await bed.AddAsync("Live", P(0.8));
        var (old, _) = await bed.AddAsync(new SeedDocument("Old", "owner", Archived: true), P(0.95));
        var (other, _) = await bed.AddAsync("Other", P(0.7));
        bed.Chat.Script("Live [1].");
        var (client, id) = await bed.NewConversationAsync();
        await client.PutAsJsonAsync($"/api/chat/conversations/{id}/scope", new { documentIds = new[] { live.DocumentId, old.DocumentId } });
        var result = await ChatBed.AskAsync(client, id);
        await client.PutAsJsonAsync($"/api/chat/conversations/{id}/scope", new { documentIds = new[] { other.DocumentId } });

        var (_, activity) = await client.GetJsonAsync(ActivityUrl(id, result.AssistantMessageId));

        var documents = activity.GetProperty("scope").GetProperty("documents").EnumerateArray()
            .ToDictionary(d => d.GetProperty("title").GetString()!, d => d.GetProperty("available").GetBoolean());
        await Assert.That(documents.Count).IsEqualTo(2);
        await Assert.That(documents["Live"]).IsTrue();
        await Assert.That(documents["Old"]).IsFalse();
    }

    // AC-10
    [Test]
    public async Task Activity_ofSomeoneElsesAnswerOrAnUnknownMessage_returns404()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Policy", P(0.9));
        bed.Chat.Script("Yes [1].");
        var (client, id) = await bed.NewConversationAsync();
        var result = await ChatBed.AskAsync(client, id);
        var stranger = bed.ClientFor(ChatRequests.NewUser());

        var others = await stranger.GetJsonAsync(ActivityUrl(id, result.AssistantMessageId));
        var unknown = await client.GetJsonAsync(ActivityUrl(id, Guid.NewGuid()));
        var userMessage = await client.GetJsonAsync(ActivityUrl(id, result.Data("question.accepted").GetProperty("userMessageId").GetGuid()));

        await Assert.That(others.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(userMessage.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    // AC-11 (stream side): the events carry what the live panel shows
    [Test]
    public async Task RetrievalCompleted_carriesScopeLeftOutScoresAndThresholds()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (live, _) = await bed.AddAsync("Live", P(0.8));
        var (old, _) = await bed.AddAsync(new SeedDocument("Old", "owner", Archived: true), P(0.95));
        bed.Chat.Script("Live [1].");
        var (client, id) = await bed.NewConversationAsync();
        await client.PutAsJsonAsync($"/api/chat/conversations/{id}/scope", new { documentIds = new[] { live.DocumentId, old.DocumentId }, tags = HrTag });

        var result = await ChatBed.AskAsync(client, id);

        var completed = result.Data("retrieval.completed");
        await Assert.That(completed.GetProperty("leftOut").EnumerateArray().Select(e => e.GetGuid()).SequenceEqual([old.DocumentId])).IsTrue();
        await Assert.That(completed.GetProperty("scope").GetProperty("documentIds").GetArrayLength()).IsEqualTo(2);
        await Assert.That(completed.GetProperty("scope").GetProperty("tags").EnumerateArray().Select(e => e.GetString()).SequenceEqual(["hr"])).IsTrue();
        await Assert.That(completed.GetProperty("minScore").GetDouble()).IsEqualTo(0.5);
        await Assert.That(completed.GetProperty("confidentScore").GetDouble()).IsEqualTo(0.7);
        await Assert.That(completed.TryGetProperty("retrievalMs", out _)).IsTrue();
        foreach (var source in completed.GetProperty("sources").EnumerateArray())
        {
            await Assert.That(source.GetProperty("score").GetDouble()).IsGreaterThan(0);
        }
    }

    // AC-8 for retries: the activity is of the latest attempt
    [Test]
    public async Task Activity_ofARetriedAnswer_isTheLatestAttempt()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Policy", P(0.9));
        bed.Chat.ThrowAfter(0);
        var (client, id) = await bed.NewConversationAsync();
        var failed = await ChatBed.AskAsync(client, id);
        bed.Chat.Script("Fixed [1].");
        await ChatBed.RetryAsync(client, id, failed.AssistantMessageId);

        var (_, activity) = await client.GetJsonAsync(ActivityUrl(id, failed.AssistantMessageId));

        await Assert.That(activity.GetProperty("answer").GetProperty("status").GetString()).IsEqualTo("complete");
    }
}
