using System.Text.Json;
using RagAssistant.Integration.Tests.Documents;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-11 and AC-12: no evidence means a refusal without any model call.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class RefusalTests(PostgresFixture postgres)
{
    [Test]
    public async Task Ask_nothingReachesMinScore_refusesWithoutCallingTheModel()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Weak policy", RetrievalBed.P(0.3));
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Types.Same("question.accepted", "retrieval.started", "retrieval.completed", "answer.completed")).IsTrue();
        var retrieval = result.Data("retrieval.completed");
        await Assert.That(retrieval.GetProperty("outcome").GetString()).IsEqualTo("noEvidence");
        await Assert.That(retrieval.GetProperty("sources").GetArrayLength()).IsEqualTo(0);
        var completed = result.Data("answer.completed");
        await Assert.That(completed.GetProperty("status").GetString()).IsEqualTo("refused");
        await Assert.That(completed.GetProperty("refusalReason").GetString()).IsEqualTo("no_evidence");
        await Assert.That(completed.GetProperty("citations").GetArrayLength()).IsEqualTo(0);
        await Assert.That(bed.Chat.CallCount).IsEqualTo(0);

        var answer = await bed.AnswerAsync(result.AssistantMessageId);
        await Assert.That(answer.Status).IsEqualTo("Refused");
        await Assert.That(answer.RefusalReason).IsEqualTo("NoEvidence");
        await Assert.That(answer.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Ask_refused_writesOneTraceWithNoModelAndNoPassageText()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);

        var traces = await bed.TracesAsync(result.AssistantMessageId);
        await Assert.That(traces.Count).IsEqualTo(1);
        var trace = traces[0];
        await Assert.That(trace[1]).IsEqualTo("NoEvidence");
        await Assert.That(trace[2]).IsNull();
        await Assert.That(trace[3]).IsEqualTo("Refused");
        await Assert.That(trace[8]).IsEqualTo("NoEvidence");
        await Assert.That(trace[9]).IsEqualTo(ChatBed.OffCorpusQuestion);
        await Assert.That(JsonDocument.Parse((string)trace[7]!).RootElement.GetArrayLength()).IsEqualTo(0);
    }
}
