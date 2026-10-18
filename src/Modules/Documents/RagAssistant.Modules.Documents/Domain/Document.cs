namespace RagAssistant.Modules.Documents.Domain;

/// <summary>Stable identity of a document across its versions.</summary>
internal sealed class Document
{
    public Guid Id { get; set; }

    public required string Title { get; set; }

    public required string OwnerName { get; set; }

    public required string Language { get; set; }

    /// <summary>Detect: an indexed version's confident detection replaces <see cref="Language"/>; Owner: detection only advises.</summary>
    public LanguageSource LanguageSource { get; set; } = LanguageSource.Detect;

    public string[] Tags { get; set; } = [];

    public DateOnly? EffectiveDate { get; set; }

    public string? Source { get; set; }

    public DocumentStatus Status { get; set; }

    /// <summary>Shared: every signed-in user; Private: only <see cref="CreatedBy"/> (ADR 0009). Changed only by the owner.</summary>
    public Visibility Visibility { get; set; } = Visibility.Shared;

    /// <summary>The indexed version that is currently retrievable; null until the first version is indexed.</summary>
    public Guid? ActiveVersionId { get; set; }

    /// <summary>Hash of the latest version's normalized content; backs the duplicate guard.</summary>
    public required string LatestContentHash { get; set; }

    /// <summary>Concurrency token for metadata edits (the ETag); bumped only by those edits, never by ingestion or archiving.</summary>
    public int Revision { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public List<DocumentVersion> Versions { get; } = [];
}
