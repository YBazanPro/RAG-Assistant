namespace RagAssistant.Modules.Chat.Domain;

/// <summary>
/// A copy of a cited passage at answer time (ADR 0004). Never updated: citations render from it, so they stay correct after the
/// document changes or is archived, and no foreign key crosses into the documents schema.
/// </summary>
internal sealed class CitationSnapshot
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    /// <summary>The <c>n</c> used in the answer text.</summary>
    public int Marker { get; set; }

    public Guid PassageId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid VersionId { get; set; }

    public int VersionNumber { get; set; }

    public required string DocumentTitle { get; set; }

    public string[] SectionPath { get; set; } = [];

    public int? PageStart { get; set; }

    public int? PageEnd { get; set; }

    public double Score { get; set; }

    /// <summary>The cited document's language at answer time (spec 018); null on snapshots written before it.</summary>
    public string? Language { get; set; }

    /// <summary>The full passage text.</summary>
    public required string Text { get; set; }
}
