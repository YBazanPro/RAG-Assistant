namespace RagAssistant.Modules.Chat.Domain;

/// <summary>A conversation belongs to the user who created it (spec 012); nobody else can see it.</summary>
internal sealed class Conversation
{
    public Guid Id { get; set; }

    /// <summary>The creator's <c>sub</c> claim, never a username or email.</summary>
    public required string OwnerSub { get; set; }

    /// <summary>Null until the first question, which names it; then changed only by a rename (spec 020).</summary>
    public string? Title { get; set; }

    /// <summary>The owner renamed it, so <see cref="Title"/> is theirs; an empty rename goes back to the first question's title.</summary>
    public bool TitleEdited { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Kept in sync with <see cref="Messages"/>, so the cap check needs no count query.</summary>
    public int MessageCount { get; set; }

    /// <summary>The documents every question of this conversation searches; empty means the whole corpus (spec 015).</summary>
    public Guid[] ScopeDocumentIds { get; set; } = [];

    /// <summary>Tags a document must all carry; they narrow the chosen documents, or the whole corpus when none are chosen.</summary>
    public string[] ScopeTags { get; set; } = [];

    public List<Message> Messages { get; } = [];
}
