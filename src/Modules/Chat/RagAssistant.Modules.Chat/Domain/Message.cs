namespace RagAssistant.Modules.Chat.Domain;

/// <summary>One message of a conversation; assistant-only fields stay null on user messages.</summary>
internal sealed class Message
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public MessageRole Role { get; set; }

    /// <summary>1-based, unique per conversation, strictly increasing.</summary>
    public int Sequence { get; set; }

    /// <summary>The question, or the answer as streamed after marker stripping; empty while pending and for refused, failed or cancelled answers.</summary>
    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string? FilterLanguage { get; set; }

    public string[] FilterTags { get; set; } = [];

    /// <summary>User messages: the query retrieval used (spec 014). Equals <see cref="Text"/> when nothing was rewritten; null on older rows.</summary>
    public string? RetrievalQuery { get; set; }

    public Guid? ReplyToMessageId { get; set; }

    public AnswerStatus? Status { get; set; }

    public RefusalReason? RefusalReason { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>When the current attempt started; the stale-answer rule measures a pending answer from here.</summary>
    public DateTimeOffset? PendingSince { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Trace rows written for this answer so far; the next attempt is this plus one.</summary>
    public int Attempts { get; set; }

    public List<CitationSnapshot> Citations { get; } = [];
}
