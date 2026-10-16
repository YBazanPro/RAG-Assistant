using System.Text.Json;
using Microsoft.Extensions.AI;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 014, AC-1 to AC-7: follow-up questions are rewritten into a standalone retrieval query, and the rewrite never breaks the answer.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class FollowUpTests(PostgresFixture postgres)
{
    private const string FollowUp = "Who approves it above that amount?";
    private const string Standalone = "Who approves a budget exception above 10,000?";

    private static string UserText(IReadOnlyList<ChatMessage> prompt) => prompt.Single(m => m.Role == ChatRole.User).Text;

    private static async Task<string?> RetrievalQueryOfAsync(ChatBed bed, Guid userMessageId) =>
        (string?)(await bed.App.RowsAsync("SELECT retrieval_query FROM chat.messages WHERE id = $1", userMessageId)).Single()[0];

    // AC-1, AC-5
    [Test]
    public async Task FollowUp_isRewrittenFromTheEarlierExchange_andOnlyTheRewriteSeesTheHistory()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9, "Exceptions need the department head."));
        bed.Chat.Script("Zebra protocol applies [1].");
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        bed.Chat.ScriptRewrite(Standalone);

        var result = await ChatBed.AskAsync(client, id, FollowUp);

        var rewritePrompt = UserText(bed.Chat.RewriteCalls.Single());
        await Assert.That(rewritePrompt).Contains(ChatBed.Question);
        await Assert.That(rewritePrompt).Contains("Zebra protocol applies.");
        await Assert.That(rewritePrompt).DoesNotContain("[1]");
        await Assert.That(rewritePrompt[..rewritePrompt.IndexOf("Current question:", StringComparison.Ordinal)]).DoesNotContain(FollowUp);
        await Assert.That(result.Data("retrieval.started").GetProperty("query").GetString()).IsEqualTo(Standalone);
        await Assert.That(result.Data("retrieval.completed").GetProperty("sources").GetArrayLength()).IsGreaterThan(0);
        await Assert.That(await RetrievalQueryOfAsync(bed, result.Data("question.accepted").GetProperty("userMessageId").GetGuid())).IsEqualTo(Standalone);

        var answerPrompt = UserText(bed.Chat.Calls[^1]);
        await Assert.That(answerPrompt).Contains("[1] Budget Exception Policy");
        await Assert.That(answerPrompt).Contains($"Question: {Standalone}");
        await Assert.That(answerPrompt).Contains($"The user wrote: {FollowUp}");
        await Assert.That(answerPrompt).DoesNotContain("Zebra");
        await Assert.That(bed.Chat.Calls[^1].Any(m => m.Text.Contains("Zebra", StringComparison.Ordinal))).IsFalse();
    }

    // AC-2
    [Test]
    public async Task FirstQuestion_and_onlyRefusedEarlierAnswers_makeNoRewriteCall()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        var first = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);
        var second = await ChatBed.AskAsync(client, id, "And the capital of New Zealand?");

        await Assert.That(first.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("refused");
        await Assert.That(bed.Chat.RewriteCalls.Count).IsEqualTo(0);
        await Assert.That(second.Data("retrieval.started").GetProperty("query").GetString()).IsEqualTo("And the capital of New Zealand?");
        await Assert.That(await RetrievalQueryOfAsync(bed, second.Data("question.accepted").GetProperty("userMessageId").GetGuid()))
            .IsEqualTo("And the capital of New Zealand?");
    }

    // AC-4
    [Test]
    [Arguments("throw", "ModelError")]
    [Arguments("client-timeout", "ModelError")]
    [Arguments("garbage", "Invalid")]
    [Arguments("empty", "Invalid")]
    [Arguments("long", "Invalid")]
    [Arguments("timeout", "Timeout")]
    public async Task FollowUp_rewriteFails_fallsBackToTheOriginalQuestionAndTheAnswerCompletes(string failure, string reason)
    {
        await using var bed = await ChatBed.StartAsync(postgres, new Dictionary<string, string> { ["Chat:FollowUp:RewriteTimeout"] = "00:00:00.300" });
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        switch (failure)
        {
            case "throw":
                bed.Chat.ThrowOnRewrite();
                break;
            case "client-timeout":
                bed.Chat.TimeOutInClientOnRewrite();
                break;
            case "garbage":
                bed.Chat.ScriptRewriteRaw("this is not json");
                break;
            case "empty":
                bed.Chat.ScriptRewrite("   ");
                break;
            case "long":
                bed.Chat.ScriptRewrite(new string('x', 1001));
                break;
            default:
                bed.Chat.HoldRewrite();
                break;
        }

        // The original wording contains "budget", so retrieval still finds the passage and the answer can complete.
        var result = await ChatBed.AskAsync(client, id, "And the budget limit above that?");

        await Assert.That(result.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("complete");
        await Assert.That(result.Data("retrieval.started").GetProperty("query").GetString()).IsEqualTo("And the budget limit above that?");
        var trace = (await bed.App.RowsAsync(
            "SELECT rewrite_used, rewrite_fallback_reason, retrieval_query FROM chat.request_traces WHERE assistant_message_id = $1",
            result.AssistantMessageId)).Single();
        await Assert.That((bool)trace[0]!).IsFalse();
        await Assert.That(trace[1]).IsEqualTo(reason);
        await Assert.That(trace[2]).IsEqualTo("And the budget limit above that?");
    }

    // AC-6
    [Test]
    public async Task Retry_runsTheRewriteAgainAndReplacesTheStoredQuery()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        bed.Chat.ScriptRewrite("What is the budget exception limit?");
        bed.Chat.ThrowAfter(0);
        var failed = await ChatBed.AskAsync(client, id, FollowUp);
        var userMessageId = failed.Data("question.accepted").GetProperty("userMessageId").GetGuid();
        await Assert.That(await RetrievalQueryOfAsync(bed, userMessageId)).IsEqualTo("What is the budget exception limit?");

        bed.Chat.ScriptRewrite(Standalone);
        await ChatBed.RetryAsync(client, id, failed.AssistantMessageId);

        await Assert.That(bed.Chat.RewriteCalls.Count).IsEqualTo(2);
        await Assert.That(await RetrievalQueryOfAsync(bed, userMessageId)).IsEqualTo(Standalone);
        var queries = (await bed.App.RowsAsync(
            "SELECT retrieval_query FROM chat.request_traces WHERE assistant_message_id = $1 ORDER BY attempt", failed.AssistantMessageId))
            .Select(r => (string)r[0]!).ToArray();
        await Assert.That(queries).IsEquivalentTo(["What is the budget exception limit?", Standalone]);
    }

    // AC-7
    [Test]
    public async Task FollowUp_cancelDuringTheRewrite_cancelsTheCallAndTheAnswer()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        bed.Chat.HoldRewrite();

        Guid assistantMessageId;
        await using (var stream = await ChatBed.OpenAsync(client, id, FollowUp))
        {
            var accepted = await stream.ReadUntilAsync(e => e.EventType == "question.accepted");
            assistantMessageId = JsonDocument.Parse(accepted.Data).RootElement.GetProperty("assistantMessageId").GetGuid();
            await bed.Chat.WaitUntilRewriteHeldAsync();
            await stream.DisconnectAsync();
        }

        await bed.Chat.RewriteCancellationObserved.WaitAsync(TimeSpan.FromSeconds(30));
        await IngestionHarness.WaitUntilAsync(
            async () => (await bed.AnswerAsync(assistantMessageId)).Status == "Cancelled", "the answer to become cancelled");
        await Assert.That(bed.Chat.CallCount).IsEqualTo(1);
    }
}
