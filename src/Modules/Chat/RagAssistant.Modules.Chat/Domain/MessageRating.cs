namespace RagAssistant.Modules.Chat.Domain;

/// <summary>The owner's verdict on one answer (spec 014): one per answer, replaced by a new one, removed by clearing it.</summary>
internal sealed class MessageRating
{
    /// <summary>The assistant message; also the key, which is what makes it one rating per answer.</summary>
    public Guid MessageId { get; set; }

    public FeedbackRating Rating { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
