namespace RagAssistant.Modules.Chat.Answering;

/// <summary>An answer whose pending rows are committed; everything the pipeline needs to produce it.</summary>
internal sealed record AcceptedAnswer(
    Guid ConversationId,
    Guid UserMessageId,
    Guid AssistantMessageId,
    string OwnerSub,
    string Question,
    string? Language,
    string[] Tags,
    Guid[] DocumentIds,
    int Attempt,
    int UserSequence);
