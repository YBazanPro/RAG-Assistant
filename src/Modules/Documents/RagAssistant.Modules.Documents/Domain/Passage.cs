using Pgvector;

namespace RagAssistant.Modules.Documents.Domain;

/// <summary>A retrievable unit of one version. Immutable once the version is indexed; IDs are never reused.</summary>
internal sealed class Passage
{
    public Guid Id { get; set; }

    public Guid VersionId { get; set; }

    public int Position { get; set; }

    /// <summary>Heading chain; passages sharing a path form the "parent section".</summary>
    public string[] SectionPath { get; set; } = [];

    public int SectionOrdinal { get; set; }

    public required string Text { get; set; }

    public int CharCount { get; set; }

    public int TokenEstimate { get; set; }

    public int? PageStart { get; set; }

    public int? PageEnd { get; set; }

    public Vector? Embedding { get; set; }
}
