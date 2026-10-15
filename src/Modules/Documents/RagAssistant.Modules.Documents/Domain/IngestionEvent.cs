namespace RagAssistant.Modules.Documents.Domain;

/// <summary>Append-only stage log. The identity id doubles as the SSE event id (step 1.4). Never holds document content.</summary>
internal sealed class IngestionEvent
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public IngestionStage Stage { get; set; }

    public EventStatus Status { get; set; }

    public string? Message { get; set; }

    /// <summary>Progress counters of a <see cref="EventStatus.Progress"/> event (pages or passages done, and the total).</summary>
    public int? Current { get; set; }

    public int? Total { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
