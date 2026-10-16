namespace RagAssistant.Modules.Chat.Common;

/// <param name="UnavailableDocumentIds">Chosen documents that retrieval cannot use right now (archived, deleted, not indexed).</param>
internal sealed record ConversationScope(Guid[] DocumentIds, string[] Tags, Guid[] UnavailableDocumentIds);

/// <summary>A conversation in a list; returned by create and list (spec 012).</summary>
public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);
