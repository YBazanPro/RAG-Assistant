namespace RagAssistant.Modules.Chat.Common;

/// <summary>A conversation in a list; returned by create and list (spec 012).</summary>
public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);
