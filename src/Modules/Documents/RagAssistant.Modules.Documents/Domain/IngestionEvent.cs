namespace RagAssistant.Modules.Documents.Domain;

/// <summary>Append-only stage log. The identity id doubles as the SSE event id (step 1.4). Never holds document content.</summary>
internal sealed class IngestionEvent
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public IngestionStage Stage { get; set; }

    public EventStatus Status { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
