using Microsoft.Extensions.AI;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Tests;

internal sealed class AnswerRulesTests
{
    [Test]
    [Arguments(EvidenceOutcome.NoEvidence, false, 0, AnswerStatus.Refused, RefusalReason.NoEvidence)]
    [Arguments(EvidenceOutcome.Sufficient, true, 0, AnswerStatus.Refused, RefusalReason.ModelDeclined)]
    [Arguments(EvidenceOutcome.LowConfidence, true, 0, AnswerStatus.Refused, RefusalReason.ModelDeclined)]
    [Arguments(EvidenceOutcome.LowConfidence, false, 2, AnswerStatus.Partial, null)]
    [Arguments(EvidenceOutcome.Sufficient, false, 1, AnswerStatus.Complete, null)]
    [Arguments(EvidenceOutcome.Sufficient, false, 0, AnswerStatus.Partial, null)]
    public async Task Decide_followsTheSpecTable(EvidenceOutcome outcome, bool sentinel, int cited, AnswerStatus status, RefusalReason? reason)
    {
        var decision = AnswerStatusRules.Decide(outcome, sentinel, cited);

        await Assert.That(decision.Status).IsEqualTo(status);
        await Assert.That(decision.Reason).IsEqualTo(reason);
    }

    [Test]
    public async Task Build_numbersPassagesWithTitleSectionPagesAndText()
    {
        RetrievedChunk[] chunks =
        [
            Chunk("Budget Exception Policy", ["Approval", "Thresholds"], 4, 5, "Exceptions need the department head."),
            Chunk("Finance Manual", [], null, null, "Finance signs off."),
        ];

        var messages = PromptBuilder.Build("Who approves?", chunks, EvidenceOutcome.Sufficient, "INSUFFICIENT_EVIDENCE");

        await Assert.That(messages.Count).IsEqualTo(2);
        var user = messages[1].Text;
        await Assert.That(user).Contains("[1] Budget Exception Policy — Approval › Thresholds (pages 4–5)\nExceptions need the department head.");
        await Assert.That(user.ReplaceLineEndings("\n")).Contains("[2] Finance Manual\nFinance signs off.");
        await Assert.That(user).EndsWith("Question: Who approves?");
        await Assert.That(messages[0].Role).IsEqualTo(ChatRole.System);
        await Assert.That(messages[0].Text).Contains("reply with exactly INSUFFICIENT_EVIDENCE and nothing else");
        await Assert.That(messages[0].Text).DoesNotContain(PromptBuilder.LowConfidenceInstruction);
    }

    [Test]
    public async Task Build_lowConfidence_addsTheIncompleteEvidenceInstruction()
    {
        var messages = PromptBuilder.Build("Who?", [Chunk("T", [], null, null, "x")], EvidenceOutcome.LowConfidence, "S");

        await Assert.That(messages[0].Text).Contains(PromptBuilder.LowConfidenceInstruction);
    }

    private static RetrievedChunk Chunk(string title, string[] section, int? pageStart, int? pageEnd, string text) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, "en", Guid.NewGuid(), 1, section, 0, pageStart, pageEnd, text, 0.9);
}
