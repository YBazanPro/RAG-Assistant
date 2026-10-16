namespace RagAssistant.Modules.Chat.Domain;

/// <summary>A conversation belongs to the user who created it (spec 012); nobody else can see it.</summary>
internal sealed class Conversation
{
    public Guid Id { get; set; }

    /// <summary>The creator's <c>sub</c> claim, never a username or email.</summary>
    public required string OwnerSub { get; set; }

    /// <summary>Null until the first question, then fixed.</summary>
    public string? Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Kept in sync with <see cref="Messages"/>, so the cap check needs no count query.</summary>
    public int MessageCount { get; set; }

    public List<Message> Messages { get; } = [];
}
