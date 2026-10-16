using RagAssistant.Modules.Chat.Common;

namespace RagAssistant.Modules.Chat.Tests;

internal sealed class ConversationTitleTests
{
    [Test]
    public async Task From_shortQuestion_keepsItWithCollapsedWhitespace()
    {
        await Assert.That(ConversationTitle.From("  Who   approves\n a budget exception? ")).IsEqualTo("Who approves a budget exception?");
    }

    [Test]
    public async Task From_longQuestion_cutsAtWordBoundaryWithEllipsis()
    {
        var question = string.Join(' ', Enumerable.Repeat("approval", 15)); // 134 characters

        var title = ConversationTitle.From(question);

        await Assert.That(title.Length).IsLessThanOrEqualTo(ConversationTitle.MaxLength);
        await Assert.That(title).EndsWith("…");
        await Assert.That(title[..^1]).IsEqualTo(string.Join(' ', Enumerable.Repeat("approval", 8)));
    }

    [Test]
    public async Task From_singleOverlongWord_isCutMidWord()
    {
        var title = ConversationTitle.From(new string('x', 120));

        await Assert.That(title).IsEqualTo(new string('x', ConversationTitle.MaxLength - 1) + "…");
    }
}
