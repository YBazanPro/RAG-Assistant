using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 019 in the answer path: AC-11, the trace side of AC-12, AC-17, and the keyword configuration of the question's language.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class RetrievalStagesTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> HybridAndRerank = new()
    {
        ["Documents:Retrieval:Hybrid:Enabled"] = "true",
        ["Documents:Retrieval:Reranking:Enabled"] = "true",
        ["Documents:Retrieval:Reranking:MinScore"] = "0.2",
        ["Documents:Retrieval:Reranking:ConfidentScore"] = "0.5",
        ["Documents:Retrieval:Reranking:Timeout"] = "00:00:01",
    };

    // English stemming matches "approves"/"approved" and "exception"/"exceptions"; the simple configuration would not.
    private const string EnglishOnlyMatch = "Budget exceptions are approved by the department head.";

    private static string ActivityUrl(Guid conversationId, Guid messageId) =>
        $"/api/chat/conversations/{conversationId}/messages/{messageId}/activity";

    // AC-11
    [Test]
    public async Task Ask_rerankScoresBelowTheirFloor_isRefusedWithoutAModelCall()
    {
        await using var bed = await ChatBed.StartAsync(postgres, HybridAndRerank);
        await bed.AddAsync("Budget Exception Policy", P(0.95, "Exceptions need the department head."));
        bed.App.Reranker.Score = (_, _) => 0.01;
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var answer = await bed.AnswerAsync(result.AssistantMessageId);
        await Assert.That(answer.Status).IsEqualTo("Refused");
        await Assert.That(answer.RefusalReason).IsEqualTo("NoEvidence");
        await Assert.That(bed.Chat.Calls.Count).IsEqualTo(0);
        await Assert.That(result.Data("retrieval.completed").GetProperty("rerank").GetString()).IsEqualTo("applied");
    }

    // AC-12, trace side
    [Test]
    public async Task Ask_rerankerTimingOut_isAnsweredFromCosineAndTheTraceSaysSkipped()
    {
        await using var bed = await ChatBed.StartAsync(postgres, HybridAndRerank);
        await bed.AddAsync("Budget Exception Policy", P(0.9, "Exceptions need the department head."));
        bed.App.Reranker.Score = (_, _) => 0.9;
        bed.App.Reranker.Delay = TimeSpan.FromSeconds(5);
        bed.Chat.Script("Department head [1].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("complete");
        var trace = (await bed.App.RowsAsync(
            "SELECT retrieval_mode, rerank_status, rerank_ms, rerank_model FROM chat.request_traces WHERE assistant_message_id = $1",
            result.AssistantMessageId)).Single();
        await Assert.That(trace[0]).IsEqualTo("hybrid");
        await Assert.That(trace[1]).IsEqualTo("skipped");
        await Assert.That((int)trace[2]!).IsGreaterThanOrEqualTo(1000);
        await Assert.That(trace[3]).IsEqualTo("fake-reranker");
    }

    // AC-17
    [Test]
    public async Task Ask_answered_showsModeRerankAndStagesInTheStreamTheAdminDetailAndTheActivity()
    {
        await using var bed = await ChatBed.StartAsync(postgres, HybridAndRerank);
        var (_, passages) = await bed.AddAsync("Budget Exception Policy", P(0.9, EnglishOnlyMatch), P(0.8, "Travel rules.", otherAxis: 2));
        bed.App.Reranker.Score = (_, text) => text.Contains("department head", StringComparison.Ordinal) ? 0.9 : 0.3;
        bed.Chat.Script("The department head [1].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var completed = result.Data("retrieval.completed");
        await Assert.That(completed.GetProperty("mode").GetString()).IsEqualTo("hybrid");
        await Assert.That(completed.GetProperty("rerank").GetString()).IsEqualTo("applied");
        var source = completed.GetProperty("sources")[0];
        await Assert.That(source.GetProperty("passageId").GetGuid()).IsEqualTo(passages[0]);
        // The keyword pool used the English configuration of the question (spec 018 detection): the stemmed match needs it.
        await Assert.That(source.GetProperty("admittedBy").GetString()).IsEqualTo("both");

        var traceId = (Guid)(await bed.App.RowsAsync(
            "SELECT id FROM chat.request_traces WHERE assistant_message_id = $1", result.AssistantMessageId)).Single()[0]!;
        using var maintainer = bed.App.CreateClientAs(Persona.Maintainer);
        var (_, detail) = await maintainer.GetJsonAsync($"/api/admin/chat/requests/{traceId}");
        var retrieval = detail.GetProperty("retrieval");
        await Assert.That(retrieval.GetProperty("mode").GetString()).IsEqualTo("hybrid");
        await Assert.That(retrieval.GetProperty("rerank").GetString()).IsEqualTo("applied");
        await Assert.That(retrieval.GetProperty("rerankMs").GetInt32()).IsGreaterThanOrEqualTo(0);
        await Assert.That(retrieval.GetProperty("rerankModel").GetString()).IsEqualTo("fake-reranker");
        var stages = retrieval.GetProperty("passages")[0].GetProperty("stages");
        await Assert.That(stages.GetProperty("rerankScore").GetDouble()).IsEqualTo(0.9);
        await Assert.That(Math.Round(stages.GetProperty("cosine").GetDouble(), 4)).IsEqualTo(0.9);
        await Assert.That(stages.GetProperty("vectorRank").GetInt32()).IsEqualTo(1);
        await Assert.That(stages.GetProperty("keywordRank").GetInt32()).IsEqualTo(1);
        await Assert.That(stages.GetProperty("admittedBy").GetString()).IsEqualTo("both");

        var (_, activity) = await client.GetJsonAsync(ActivityUrl(id, result.AssistantMessageId));
        var step = activity.GetProperty("retrieval");
        await Assert.That(step.GetProperty("mode").GetString()).IsEqualTo("hybrid");
        await Assert.That(step.GetProperty("rerank").GetString()).IsEqualTo("applied");
        await Assert.That(step.GetProperty("passages")[0].GetProperty("admittedBy").GetString()).IsEqualTo("both");
        // The activity stays free of rerank numbers and passage text (spec 015, AC-14).
        await Assert.That(step.GetProperty("passages")[0].TryGetProperty("stages", out _)).IsFalse();
        await Assert.That(activity.GetRawText()).DoesNotContain(EnglishOnlyMatch);
    }

    [Test]
    public async Task Ask_defaultConfiguration_isVectorWithRerankingOff()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9, EnglishOnlyMatch));
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var completed = result.Data("retrieval.completed");
        await Assert.That(completed.GetProperty("mode").GetString()).IsEqualTo("vector");
        await Assert.That(completed.GetProperty("rerank").GetString()).IsEqualTo("off");
        await Assert.That(completed.GetProperty("sources")[0].GetProperty("admittedBy").GetString()).IsEqualTo("vector");
        await Assert.That(bed.App.Reranker.Calls.Count).IsEqualTo(0);
        var json = JsonSerializer.Serialize(completed);
        await Assert.That(json).DoesNotContain("keywordRank");
    }
}
