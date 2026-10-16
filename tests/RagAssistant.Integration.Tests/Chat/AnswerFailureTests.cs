using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-20 to AC-23 and AC-27: failures after question.accepted, disconnects and heartbeats.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class AnswerFailureTests(PostgresFixture postgres)
{
    [Test]
    public async Task Ask_embeddingModelDown_sendsARetryableErrorAndFailsTheAnswer()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Generator.AlwaysFail = true;
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Types.Same("question.accepted", "retrieval.started", "error")).IsTrue();
        var error = result.Data("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("retrieval.embedding_unavailable");
        await Assert.That(error.GetProperty("retryable").GetBoolean()).IsTrue();
        var answer = await bed.AnswerAsync(result.AssistantMessageId);
        await Assert.That(answer.Status).IsEqualTo("Failed");
        await Assert.That(answer.ErrorCode).IsEqualTo("retrieval.embedding_unavailable");
        await Assert.That((await bed.TracesAsync(result.AssistantMessageId)).Single()[4]).IsEqualTo("retrieval.embedding_unavailable");
        await Assert.That(bed.Chat.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Ask_noFirstTokenWithinTheDeadline_failsWithModelTimeout()
    {
        await using var bed = await ChatBed.StartAsync(postgres, new Dictionary<string, string>
        {
            ["Chat:Generation:FirstTokenTimeout"] = "00:00:00.300",
            ["Chat:Generation:TotalTimeout"] = "00:00:10",
        });
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.HoldBeforeFirstToken();
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var error = result.Data("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("chat.model_timeout");
        await Assert.That(error.GetProperty("retryable").GetBoolean()).IsTrue();
        await Assert.That((await bed.AnswerAsync(result.AssistantMessageId)).Status).IsEqualTo("Failed");
    }

    [Test]
    public async Task Ask_modelFailsMidStream_keepsTheSentDeltasButPersistsNoText()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.Script("Department ", "head ", "and Finance [1].");
        bed.Chat.ThrowAfter(2);
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Deltas).IsEqualTo("Department head ");
        await Assert.That(result.Types[^1]).IsEqualTo("error");
        await Assert.That(result.Data("error").GetProperty("code").GetString()).IsEqualTo("chat.model_unavailable");
        var answer = await bed.AnswerAsync(result.AssistantMessageId);
        await Assert.That(answer.Status).IsEqualTo("Failed");
        await Assert.That(answer.ErrorCode).IsEqualTo("chat.model_unavailable");
        await Assert.That(answer.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Ask_clientDisconnectsWhileTheModelWorks_cancelsTheCallAndTheAnswer()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.HoldBeforeFirstToken();
        var (client, id) = await bed.NewConversationAsync();

        Guid assistantMessageId;
        await using (var stream = await ChatBed.OpenAsync(client, id))
        {
            var accepted = await stream.ReadUntilAsync(e => e.EventType == "question.accepted");
            assistantMessageId = JsonDocument.Parse(accepted.Data).RootElement.GetProperty("assistantMessageId").GetGuid();
            await stream.ReadUntilAsync(e => e.EventType == "retrieval.completed");
            await bed.Chat.WaitUntilHeldAsync();
            await stream.DisconnectAsync();
        }

        await bed.Chat.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(30));
        await IngestionHarness.WaitUntilAsync(
            async () => (await bed.AnswerAsync(assistantMessageId)).Status == "Cancelled", "the answer to become cancelled");
        await Assert.That((await bed.AnswerAsync(assistantMessageId)).Text).IsEqualTo(string.Empty);
        await IngestionHarness.WaitUntilAsync(async () => (await bed.TracesAsync(assistantMessageId)).Count == 1, "the trace");
        await Assert.That((await bed.TracesAsync(assistantMessageId)).Single()[3]).IsEqualTo("Cancelled");
    }

    [Test]
    public async Task Ask_modelSlowerThanTheHeartbeatInterval_sendsHeartbeatsBeforeTheFirstDelta()
    {
        await using var bed = await ChatBed.StartAsync(postgres, new Dictionary<string, string>
        {
            ["Chat:Generation:HeartbeatInterval"] = "00:00:00.100",
        });
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.HoldBeforeFirstToken();
        var (client, id) = await bed.NewConversationAsync();

        await using var stream = await ChatBed.OpenAsync(client, id);
        await stream.ReadUntilAsync(e => e.EventType == "heartbeat");
        bed.Chat.Release();
        await stream.ReadToEndAsync();

        var types = stream.Received.Select(e => e.EventType).ToList();
        await Assert.That(types.IndexOf("heartbeat")).IsLessThan(types.IndexOf("answer.delta"));
        await Assert.That(types[^1]).IsEqualTo("answer.completed");
    }
}
