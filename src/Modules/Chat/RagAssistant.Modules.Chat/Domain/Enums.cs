namespace RagAssistant.Modules.Chat.Domain;

internal enum MessageRole
{
    User,
    Assistant,
}

internal enum AnswerStatus
{
    Pending,
    Complete,
    Partial,
    Refused,
    Failed,
    Cancelled,
}

internal enum RefusalReason
{
    NoEvidence,
    ModelDeclined,
}

/// <summary>The wire names (spec 012: enums as lowercase strings).</summary>
internal static class ChatApiNames
{
    public static string ToApi(this MessageRole role) => role == MessageRole.User ? "user" : "assistant";

    public static string ToApi(this AnswerStatus status) => status.ToString().ToLowerInvariant();

    public static string ToApi(this RefusalReason reason) => reason == RefusalReason.NoEvidence ? "no_evidence" : "model_declined";
}
