namespace RagAssistant.Modules.Documents.Domain;

internal sealed class DocumentVersion
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public int Number { get; set; }

    public VersionOrigin Origin { get; set; }

    public VersionStatus Status { get; set; }

    public SourceFormat SourceFormat { get; set; }

    public required string OriginalFileName { get; set; }

    public string? ContentType { get; set; }

    /// <summary>Normalized UTF-8 source: no BOM, LF line endings.</summary>
    public required string Content { get; set; }

    public required string ContentHash { get; set; }

    public int SizeBytes { get; set; }

    public string? EmbeddingModel { get; set; }

    public int? EmbeddingDimensions { get; set; }

    public int? PassageCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset? IndexedAt { get; set; }

    public List<IngestionRun> Runs { get; } = [];
}
