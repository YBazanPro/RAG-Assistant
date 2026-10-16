using System.Text.Json;
using Microsoft.Extensions.AI;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-6 and AC-13 to AC-19: the grounded answer, its citations, the sentinel and the prompt.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class GroundedAnswerTests(PostgresFixture postgres)
{
    [Test]
    public async Task Ask_sufficientEvidence_streamsSourcesThenTheTextAndCompletesWithTheCitedSnapshots()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (doc, passages) = await bed.AddAsync(
            "Budget Exception Policy",
            P(0.9, "Exceptions need the department head.", section: ["Approval"]),
            P(0.85, "Exceptions are rare.", otherAxis: 2),
            P(0.8, "Finance signs off on every exception.", otherAxis: 3, page: 4));
        bed.Chat.Script("Department head ", "[", "1] and Fin", "ance [3", "].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var types = result.Types;
        await Assert.That(types[..3].Same("question.accepted", "retrieval.started", "retrieval.completed")).IsTrue();
        await Assert.That(types[^1]).IsEqualTo("answer.completed");
        await Assert.That(types[3..^1].All(t => t == "answer.delta")).IsTrue();
        var sources = result.Data("retrieval.completed").GetProperty("sources").EnumerateArray().ToArray();
        await Assert.That(sources.Select(s => s.GetProperty("marker").GetInt32()).Same(1, 2, 3)).IsTrue();
        await Assert.That(sources[0].GetProperty("passageId").GetGuid()).IsEqualTo(passages[0]);
        await Assert.That(result.Deltas).IsEqualTo("Department head [1] and Finance [3].");

        var completed = result.Data("answer.completed");
        await Assert.That(completed.GetProperty("status").GetString()).IsEqualTo("complete");
        var citations = completed.GetProperty("citations").EnumerateArray().ToArray();
        await Assert.That(citations.Select(c => c.GetProperty("marker").GetInt32()).Same(1, 3)).IsTrue();
        await Assert.That(citations[1].GetProperty("passageId").GetGuid()).IsEqualTo(passages[2]);
        await Assert.That(citations[1].GetProperty("text").GetString()).IsEqualTo("Finance signs off on every exception.");
        await Assert.That(citations[1].GetProperty("pageStart").GetInt32()).IsEqualTo(4);
        await Assert.That(citations[0].GetProperty("documentId").GetGuid()).IsEqualTo(doc.DocumentId);

        var answer = await bed.AnswerAsync(result.AssistantMessageId);
        await Assert.That(answer.Status).IsEqualTo("Complete");
        await Assert.That(answer.Text).IsEqualTo("Department head [1] and Finance [3].");
        var snapshotTexts = await bed.App.RowsAsync(
            "SELECT marker, text FROM chat.citation_snapshots WHERE message_id = $1 ORDER BY marker", result.AssistantMessageId);
        await Assert.That(snapshotTexts.Select(r => (int)r[0]!).Same(1, 3)).IsTrue();
        await Assert.That((string)snapshotTexts[0][1]!).IsEqualTo("Exceptions need the department head.");
    }

    [Test]
    public async Task Ask_markerOutsideTheSources_isDroppedInTheStreamAndCounted()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9), P(0.85, otherAxis: 2), P(0.8, otherAxis: 3));
        bed.Chat.Script("Rule [1] and ", "[", "7].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Deltas).IsEqualTo("Rule [1] and .");
        var citations = result.Data("answer.completed").GetProperty("citations").EnumerateArray().ToArray();
        await Assert.That(citations.Select(c => c.GetProperty("marker").GetInt32()).Same(1)).IsTrue();
        var trace = (await bed.TracesAsync(result.AssistantMessageId)).Single();
        await Assert.That(trace[6]).IsEqualTo(1);
        var retrieved = JsonDocument.Parse((string)trace[7]!).RootElement.EnumerateArray().ToArray();
        await Assert.That(retrieved.Select(p => p.GetProperty("cited").GetBoolean()).Same(true, false, false)).IsTrue();
    }

    [Test]
    public async Task Ask_markerList_isNormalizedAndOutOfRangeNumbersAreDropped()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9), P(0.85, otherAxis: 2), P(0.8, otherAxis: 3));
        bed.Chat.Script("Both apply [1, ", "3] and [2,7", "].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Deltas).IsEqualTo("Both apply [1][3] and [2].");
        var citations = result.Data("answer.completed").GetProperty("citations").EnumerateArray();
        await Assert.That(citations.Select(c => c.GetProperty("marker").GetInt32()).Same(1, 2, 3)).IsTrue();
        await Assert.That((await bed.TracesAsync(result.AssistantMessageId)).Single()[6]).IsEqualTo(1);
    }

    [Test]
    public async Task Ask_lowConfidence_isPartialAndThePromptAsksToSayWhatIsNotCovered()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.6));
        bed.Chat.Script("Probably the department head [1].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Data("retrieval.completed").GetProperty("outcome").GetString()).IsEqualTo("lowConfidence");
        await Assert.That(result.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("partial");
        var system = bed.Chat.Calls.Single().Single(m => m.Role == ChatRole.System).Text;
        await Assert.That(system).Contains("say what they do not cover");
    }

    [Test]
    public async Task Ask_sufficientEvidenceButNoValidMarker_isPartialWithoutCitations()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.Script("The department head approves it.");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var completed = result.Data("answer.completed");
        await Assert.That(completed.GetProperty("status").GetString()).IsEqualTo("partial");
        await Assert.That(completed.GetProperty("citations").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Ask_replyIsTheSentinel_refusesAsModelDeclinedWithoutAnyDelta()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.Script("INSUFF", "ICIENT_EVIDENCE.");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Types.Contains("answer.delta")).IsFalse();
        var completed = result.Data("answer.completed");
        await Assert.That(completed.GetProperty("status").GetString()).IsEqualTo("refused");
        await Assert.That(completed.GetProperty("refusalReason").GetString()).IsEqualTo("model_declined");
        await Assert.That((await bed.AnswerAsync(result.AssistantMessageId)).Text).IsEqualTo(string.Empty);
        await Assert.That((bool)(await bed.TracesAsync(result.AssistantMessageId)).Single()[5]!).IsTrue();
    }

    [Test]
    public async Task Ask_normalFirstToken_isReleasedAtOnce()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        bed.Chat.Script("Department", " head [1].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var first = result.Events.First(e => e.EventType == "answer.delta");
        await Assert.That(JsonDocument.Parse(first.Data).RootElement.GetProperty("text").GetString()).IsEqualTo("Department");
    }

    [Test]
    public async Task Ask_secondQuestion_promptHasThePassagesAndTheSentinelButNoEarlierMessage()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9, "Exceptions need the department head.", section: ["Approval", "Thresholds"]));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id, "First budget question?");

        await ChatBed.AskAsync(client, id, "Second budget question?");

        var prompt = bed.Chat.Calls[1];
        var user = prompt.Single(m => m.Role == ChatRole.User).Text;
        var system = prompt.Single(m => m.Role == ChatRole.System).Text;
        await Assert.That(user).Contains("[1] Budget Exception Policy — Approval › Thresholds");
        await Assert.That(user).Contains("Exceptions need the department head.");
        await Assert.That(user).Contains("Question: Second budget question?");
        await Assert.That(user).DoesNotContain("First budget question?");
        await Assert.That(prompt.Count).IsEqualTo(2);
        await Assert.That(system).Contains("INSUFFICIENT_EVIDENCE");
    }

    [Test]
    public async Task Get_conversationWithTwoAnswers_returnsMessagesInOrderWithStatusAndSnapshots()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9, "Exceptions need the department head."));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);

        var (_, body) = await client.GetJsonAsync($"/api/chat/conversations/{id}");

        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        await Assert.That(messages.Select(m => m.GetProperty("sequence").GetInt32()).Same(1, 2, 3, 4)).IsTrue();
        await Assert.That(messages.Select(m => m.GetProperty("role").GetString()).Same("user", "assistant", "user", "assistant")).IsTrue();
        await Assert.That(messages[1].GetProperty("status").GetString()).IsEqualTo("complete");
        await Assert.That(messages[1].GetProperty("replyToMessageId").GetGuid()).IsEqualTo(messages[0].GetProperty("id").GetGuid());
        await Assert.That(messages[1].GetProperty("citations")[0].GetProperty("text").GetString()).IsEqualTo("Exceptions need the department head.");
        await Assert.That(messages[3].GetProperty("status").GetString()).IsEqualTo("refused");
        await Assert.That(messages[3].GetProperty("refusalReason").GetString()).IsEqualTo("no_evidence");
    }
}
