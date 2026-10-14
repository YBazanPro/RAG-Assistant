namespace RagAssistant.Modules.Documents.Domain;

/// <summary>One attempt to ingest a version. The persisted row is the source of truth for the background worker.</summary>
internal sealed class IngestionRun
{
    public Guid Id { get; set; }

    public Guid VersionId { get; set; }

    public int Attempt { get; set; }

    public RunStatus Status { get; set; }

    public IngestionStage CurrentStage { get; set; }

    public DateTimeOffset QueuedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public List<IngestionEvent> Events { get; } = [];
}
