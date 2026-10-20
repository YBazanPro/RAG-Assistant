using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 021, AC-4: passages that do not fit the context window are left out before the sources are announced.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class ContextBudgetTests(PostgresFixture postgres)
{
    // 1,200 tokens of window minus 100 for the answer: the rules and one ~500-token passage fit, a second one does not.
    private static readonly Dictionary<string, string> TightWindow = new()
    {
        ["Chat:Generation:ContextWindowTokens"] = "1200",
        ["Chat:Generation:MaxOutputTokens"] = "100",
    };

    /// <summary>About 1,500 characters of words no other passage shares, so near-duplicate reduction keeps every passage.</summary>
    private static string LongText(int seed) => string.Join(' ', Enumerable.Range(0, 250).Select(i => $"w{seed}x{i}"));

    [Test]
    public async Task Ask_promptOverTheWindow_sendsOnlyTheBestPassagesAndRecordsTheTrim()
    {
        await using var bed = await ChatBed.StartAsync(postgres, TightWindow);
        var (_, passages) = await bed.AddAsync(
            "Budget Exception Policy",
            P(0.9, LongText(1)),
            P(0.85, LongText(2), otherAxis: 2),
            P(0.8, LongText(3), otherAxis: 3));
        bed.Chat.Script("Rule [1] and [2].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        var sources = result.Data("retrieval.completed").GetProperty("sources").EnumerateArray().ToArray();
        await Assert.That(sources.Length).IsEqualTo(1);
        await Assert.That(sources[0].GetProperty("passageId").GetGuid()).IsEqualTo(passages[0]);
        await Assert.That(result.Deltas).IsEqualTo("Rule [1] and .");
        var citations = result.Data("answer.completed").GetProperty("citations").EnumerateArray().ToArray();
        await Assert.That(citations.Single().GetProperty("passageId").GetGuid()).IsEqualTo(passages[0]);

        var user = bed.Chat.Calls.Single().Single(m => m.Role == Microsoft.Extensions.AI.ChatRole.User).Text;
        await Assert.That(user).Contains(LongText(1));
        await Assert.That(user).DoesNotContain("w2x0 ");

        var trace = (await bed.App.RowsAsync(
            "SELECT id, trimmed_passage_count, prompt_token_estimate FROM chat.request_traces WHERE assistant_message_id = $1",
            result.AssistantMessageId)).Single();
        await Assert.That(trace[1]).IsEqualTo(2);
        await Assert.That((int)trace[2]!).IsGreaterThan(0);
        await Assert.That((int)trace[2]!).IsLessThanOrEqualTo(1200 - 100);

        using var maintainer = bed.App.CreateClientAs(Persona.Maintainer);
        var (_, detail) = await maintainer.GetJsonAsync($"/api/admin/chat/requests/{(Guid)trace[0]!}");
        var generation = detail.GetProperty("generation");
        await Assert.That(generation.GetProperty("trimmedPassageCount").GetInt32()).IsEqualTo(2);
        await Assert.That(generation.GetProperty("promptTokenEstimate").GetInt32()).IsEqualTo((int)trace[2]!);
    }

    [Test]
    public async Task Ask_promptInsideTheWindow_sendsEveryPassage()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9), P(0.85, otherAxis: 2), P(0.8, otherAxis: 3));
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Data("retrieval.completed").GetProperty("sources").GetArrayLength()).IsEqualTo(3);
        var trace = (await bed.App.RowsAsync(
            "SELECT trimmed_passage_count, prompt_token_estimate FROM chat.request_traces WHERE assistant_message_id = $1",
            result.AssistantMessageId)).Single();
        await Assert.That(trace[0]).IsEqualTo(0);
        await Assert.That(trace[1]).IsNotNull();
    }

    [Test]
    public async Task Ask_noEvidence_buildsNoPromptAndRecordsNoEstimate()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);

        var trace = (await bed.App.RowsAsync(
            "SELECT trimmed_passage_count, prompt_token_estimate FROM chat.request_traces WHERE assistant_message_id = $1",
            result.AssistantMessageId)).Single();
        await Assert.That(trace[0]).IsEqualTo(0);
        await Assert.That(trace[1]).IsNull();
    }
}
