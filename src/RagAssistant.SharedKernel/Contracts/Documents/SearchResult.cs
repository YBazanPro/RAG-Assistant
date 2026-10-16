namespace RagAssistant.SharedKernel.Contracts.Documents;

/// <param name="Language">Two letters; matched case-insensitively against the document language.</param>
/// <param name="Tags">The document must carry all of them (normalized like import).</param>
/// <param name="DocumentIds">Null searches every document; a list (even an empty one) searches only those documents (spec 015).</param>
public sealed record SearchFilters(string? Language = null, IReadOnlyList<string>? Tags = null, IReadOnlyList<Guid>? DocumentIds = null)
{
    public static SearchFilters None { get; } = new();
}

public enum EvidenceOutcome
{
    /// <summary>No passage reached the minimum score: refuse.</summary>
    NoEvidence,

    /// <summary>The best passage is below the confident score: a partial answer at most.</summary>
    LowConfidence,

    Sufficient,
}

public sealed record EvidenceThresholds(double MinScore, double ConfidentScore, double DuplicateOverlap);

/// <param name="Chunks">Kept passages, best first; empty when <see cref="EvidenceOutcome.NoEvidence"/>.</param>
/// <param name="Thresholds">The values applied, so a caller can record the decision.</param>
public sealed record SearchResult(
    EvidenceOutcome Outcome,
    IReadOnlyList<RetrievedChunk> Chunks,
    EvidenceThresholds Thresholds,
    string EmbeddingModel,
    double? BestScore = null);

/// <param name="Text">The full passage text.</param>
/// <param name="Score">Cosine similarity to the query (1 − cosine distance); higher is better.</param>
public sealed record RetrievedChunk(
    Guid PassageId,
    Guid DocumentId,
    string DocumentTitle,
    string Language,
    Guid VersionId,
    int VersionNumber,
    IReadOnlyList<string> SectionPath,
    int Position,
    int? PageStart,
    int? PageEnd,
    string Text,
    double Score);
