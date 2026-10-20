using RagAssistant.Modules.Chat.Features.ExportConversation;

namespace RagAssistant.Modules.Chat.Tests;

internal sealed class ExportSlugTests
{
    // Spec 020: the export file is named <slug>-<date>.md.
    [Test]
    [Arguments("Who approves a budget exception?", "who-approves-a-budget-exception")]
    [Arguments("Congés payés : combien de jours ?", "conges-payes-combien-de-jours")]
    [Arguments("???", "conversation")]
    public async Task Slug_foldsAccentsAndJoinsWordsWithHyphens(string title, string expected) =>
        await Assert.That(ExportConversation.Slug(title)).IsEqualTo(expected);
}
