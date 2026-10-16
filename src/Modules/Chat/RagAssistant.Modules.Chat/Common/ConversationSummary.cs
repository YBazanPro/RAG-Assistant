using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Common;

/// <param name="UnavailableDocumentIds">Chosen documents that retrieval cannot use right now (archived, deleted, not indexed).</param>
/// <param name="Documents">The chosen documents with their current titles, for the chips of the chat page.</param>
internal sealed record ConversationScope(Guid[] DocumentIds, string[] Tags, Guid[] UnavailableDocumentIds, ScopeDocumentView[] Documents)
{
    public static ConversationScope From(Guid[] documentIds, string[] tags, DocumentAvailability availability) => new(
        documentIds,
        tags,
        [.. documentIds.Where(id => !availability.Searchable.Contains(id))],
        [.. documentIds.Select(id => new ScopeDocumentView(id, availability.Titles.GetValueOrDefault(id), availability.Searchable.Contains(id)))]);
}

internal sealed record ScopeDocumentView(Guid Id, string? Title, bool Available);

/// <summary>A conversation in a list; returned by create and list (spec 012).</summary>
public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);
