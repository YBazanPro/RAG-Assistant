using System.Net;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-24 to AC-26: retrying the last answer, and pending answers left behind by a crash.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class RetryAndSweepTests(PostgresFixture postgres)
{
    [Test]
    public async Task Retry_lastAnswerFailed_streamsANewAnswerIntoTheSameMessageWithASecondTrace()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.ThrowAfter(0);
        var (client, id) = await bed.NewConversationAsync();
        var failed = await ChatBed.AskAsync(client, id);
        var assistantMessageId = failed.AssistantMessageId;

        var retried = await ChatBed.RetryAsync(client, id, assistantMessageId);

        var accepted = retried.Data("question.accepted");
        await Assert.That(accepted.GetProperty("assistantMessageId").GetGuid()).IsEqualTo(assistantMessageId);
        await Assert.That(accepted.GetProperty("userMessageId").GetGuid())
            .IsEqualTo(failed.Data("question.accepted").GetProperty("userMessageId").GetGuid());
        await Assert.That(retried.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("complete");
        var answer = await bed.AnswerAsync(assistantMessageId);
        await Assert.That(answer.Status).IsEqualTo("Complete");
        await Assert.That(answer.ErrorCode).IsNull();
        await Assert.That(answer.Text).IsEqualTo("Department head and Finance [1].");
        await Assert.That(await bed.App.ScalarAsync<int>("SELECT message_count FROM chat.conversations WHERE id = $1", id)).IsEqualTo(2);
        var traces = await bed.TracesAsync(assistantMessageId);
        await Assert.That(traces.Select(t => (int)t[0]!).Same(1, 2)).IsTrue();
        await Assert.That(traces.Select(t => (string)t[3]!).Same("Failed", "Complete")).IsTrue();
    }

    [Test]
    public async Task Retry_completeAnswerOrAnEarlierFailedOne_returns409RetryNotAllowed()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.ThrowAfter(0);
        var (client, id) = await bed.NewConversationAsync();
        var earlierFailed = await ChatBed.AskAsync(client, id);
        var complete = await ChatBed.AskAsync(client, id);

        var onComplete = await ChatBed.RetryAsync(client, id, complete.AssistantMessageId);
        var onEarlier = await ChatBed.RetryAsync(client, id, earlierFailed.AssistantMessageId);

        await Assert.That(onComplete.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(onComplete.Problem.GetProperty("code").GetString()).IsEqualTo("chat.retry_not_allowed");
        await Assert.That(onEarlier.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(onEarlier.Problem.GetProperty("code").GetString()).IsEqualTo("chat.retry_not_allowed");
    }

    [Test]
    public async Task Retry_unknownMessage_returns404MessageNotFound()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.RetryAsync(client, id, Guid.NewGuid());

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(result.Problem.GetProperty("code").GetString()).IsEqualTo("chat.message_not_found");
    }

    [Test]
    public async Task Startup_pendingAnswersOfThePreviousProcess_becomeFailedInterrupted()
    {
        var database = $"chat_{Guid.NewGuid():N}";
        Guid pending;
        await using (var before = await ChatBed.StartAsync(postgres, databaseName: database))
        {
            var (client, id) = await before.NewConversationAsync();
            pending = await InsertPendingAsync(before, id, minutesAgo: 0);
        }

        await using var after = await ChatBed.StartAsync(postgres, databaseName: database);
        await IngestionHarness.WaitUntilAsync(async () => (await after.AnswerAsync(pending)).Status == "Failed", "the startup sweep");

        await Assert.That((await after.AnswerAsync(pending)).ErrorCode).IsEqualTo("chat.interrupted");
    }

    [Test]
    public async Task Ask_pendingAnswerOlderThanTheDeadline_isFailedAndTheQuestionAccepted()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (client, id) = await bed.NewConversationAsync();
        var stale = await InsertPendingAsync(bed, id, minutesAgo: 10);

        var result = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        var answer = await bed.AnswerAsync(stale);
        await Assert.That(answer.Status).IsEqualTo("Failed");
        await Assert.That(answer.ErrorCode).IsEqualTo("chat.interrupted");
    }

    /// <summary>A user question and its pending answer, as a crashed process would leave them.</summary>
    private static async Task<Guid> InsertPendingAsync(ChatBed bed, Guid conversationId, int minutesAgo)
    {
        var question = Guid.CreateVersion7();
        var answer = Guid.CreateVersion7();
        await bed.App.ExecuteAsync(
            """
            INSERT INTO chat.messages (id, conversation_id, role, sequence, text, created_at, filter_tags, attempts)
            VALUES ($1, $2, 'User', 1, 'Who approves it?', now(), '{}', 0)
            """,
            question, conversationId);
        await bed.App.ExecuteAsync(
            """
            INSERT INTO chat.messages (id, conversation_id, role, sequence, text, created_at, filter_tags, reply_to_message_id, status, pending_since, attempts)
            VALUES ($1, $2, 'Assistant', 2, '', now(), '{}', $3, 'Pending', now() - make_interval(mins => $4), 1)
            """,
            answer, conversationId, question, minutesAgo);
        await bed.App.ExecuteAsync("UPDATE chat.conversations SET message_count = 2 WHERE id = $1", conversationId);
        return answer;
    }
}
