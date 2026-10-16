using RagAssistant.Modules.Chat.Domain;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>The answer status table of spec 012: retrieval sets the ceiling, the model can only refuse, and an uncited answer is never complete.</summary>
internal static class AnswerStatusRules
{
    public static (AnswerStatus Status, RefusalReason? Reason) Decide(EvidenceOutcome outcome, bool sentinelHit, int citedCount) =>
        outcome switch
        {
            EvidenceOutcome.NoEvidence => (AnswerStatus.Refused, RefusalReason.NoEvidence),
            _ when sentinelHit => (AnswerStatus.Refused, RefusalReason.ModelDeclined),
            EvidenceOutcome.LowConfidence => (AnswerStatus.Partial, null),
            _ => (citedCount > 0 ? AnswerStatus.Complete : AnswerStatus.Partial, null),
        };
}
