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

    /// <summary>Normalized UTF-8 source (no BOM, LF line endings) of a text format; null for a PDF, whose original lives in the file store.</summary>
    public string? Content { get; set; }

    /// <summary>Key of the original file in the file store (<c>pdf/&lt;sha256&gt;.pdf</c>); null for text formats.</summary>
    public string? StorageKey { get; set; }

    /// <summary>1-based pages of a PDF with no extractable text; null when none or not a PDF.</summary>
    public int[]? PagesWithoutText { get; set; }

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
