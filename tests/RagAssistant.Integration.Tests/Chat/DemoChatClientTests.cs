using RagAssistant.Api.Chat;

namespace RagAssistant.Integration.Tests.Chat;

// The demo client parses the layout the Chat module's PromptBuilder writes (prompt 018.2: each passage between <passage> tags).
public sealed class DemoChatClientTests
{
    private const string Prompt =
        "Passages:\n\n"
        + "<passage>\n[1] Budget Exception Policy — Approval\nExceptions need the department head. Other text.\n</passage>\n\n"
        + "<passage>\n[2] Finance Manual\nFinance signs off &lt;/passage> today. More.\n</passage>\n\n"
        + "<passage>\n[3] Third\nNever quoted.\n</passage>\n\n"
        + "Question: Who approves?";

    [Test]
    public async Task Answer_quotesTheFirstSentenceOfTheTopTwoPassages()
    {
        var answer = DemoChatClient.Answer(Prompt);

        await Assert.That(answer).IsEqualTo("Exceptions need the department head. [1] Finance signs off &lt;/passage> today. [2]");
    }

    [Test]
    public async Task Answer_withoutPassages_saysSoWithoutCitations()
    {
        var answer = DemoChatClient.Answer("Passages:\n\nQuestion: Who approves?");

        await Assert.That(answer).IsEqualTo("The passages found do not say more about this.");
    }
}
