namespace RagAssistant.Modules.Chat.Domain;

/// <summary>A problem the owner reported on one answer (spec 014). Append-only; at most a few per answer.</summary>
internal sealed class IssueReport
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public IssueCategory Category { get; set; }

    /// <summary>Trimmed; null when empty.</summary>
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
