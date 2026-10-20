namespace RagAssistant.SharedKernel.Contracts.Documents;

/// <param name="Language">Two letters; matched case-insensitively against the document language.</param>
/// <param name="Tags">The document must carry all of them (normalized like import).</param>
/// <param name="DocumentIds">Null searches every document; a list (even an empty one) searches only those documents (spec 015).</param>
/// <param name="QueryLanguage">
/// Two letters: the language the query is written in, which picks the keyword search configuration (spec 019); null uses the
/// language-neutral one. It never restricts which documents are searched.
/// </param>
public sealed record SearchFilters(
    string? Language = null, IReadOnlyList<string>? Tags = null, IReadOnlyList<Guid>? DocumentIds = null, string? QueryLanguage = null)
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

/// <summary>How the candidate pool was found (spec 019).</summary>
public enum RetrievalMode
{
    Vector,

    /// <summary>Vector and keyword pools merged with reciprocal rank fusion.</summary>
    Hybrid,
}

/// <summary>Whether the cross-encoder reordered and gated the candidates (spec 019).</summary>
public enum RerankStatus
{
    Off,
    Applied,

    /// <summary>Reranking was asked for, but the reranker failed or is not configured; cosine gated the result.</summary>
    Skipped,
}

/// <summary>Which pool admitted a passage (spec 019).</summary>
public enum AdmittedBy
{
    Vector,
    Keyword,
    Both,
}

public sealed record EvidenceThresholds(double MinScore, double ConfidentScore, double DuplicateOverlap);

/// <param name="Chunks">Kept passages, best first; empty when <see cref="EvidenceOutcome.NoEvidence"/>.</param>
/// <param name="Thresholds">The values applied, so a caller can record the decision: the rerank thresholds when reranking was applied.</param>
/// <param name="RerankModel">The cross-encoder that was asked; null when reranking was off.</param>
public sealed record SearchResult(
    EvidenceOutcome Outcome,
    IReadOnlyList<RetrievedChunk> Chunks,
    EvidenceThresholds Thresholds,
    string EmbeddingModel,
    double? BestScore = null,
    RetrievalMode Mode = RetrievalMode.Vector,
    RerankStatus Rerank = RerankStatus.Off,
    int? RerankMs = null,
    string? RerankModel = null);

/// <param name="Text">The full passage text.</param>
/// <param name="Score">
/// The gating score, higher is better: the cross-encoder's score when reranking was applied, otherwise the cosine similarity to the
/// query (1 − cosine distance). <see cref="Stages"/> holds each stage's own number.
/// </param>
/// <param name="Stages">What each retrieval stage said about the passage (spec 019); null only for chunks built outside retrieval.</param>
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
    double Score,
    PassageStages? Stages = null);

/// <summary>What each retrieval stage said about a passage (spec 019); a stage that did not see the passage leaves its fields null.</summary>
/// <param name="VectorRank">1-based rank in the vector pool.</param>
/// <param name="Cosine">Cosine similarity to the query, known for every candidate.</param>
/// <param name="KeywordRank">1-based rank in the keyword pool.</param>
/// <param name="KeywordScore"><c>ts_rank_cd</c> of the passage for the keyword query.</param>
/// <param name="FusedScore">Reciprocal rank fusion score; null in vector mode.</param>
/// <param name="RerankScore">The cross-encoder's score; null when the passage was not reranked.</param>
/// <param name="KeywordCoverage">The share of the question's distinct terms the passage contains; null without a keyword match.</param>
public sealed record PassageStages(
    int? VectorRank,
    double Cosine,
    int? KeywordRank,
    double? KeywordScore,
    double? FusedScore,
    double? RerankScore,
    AdmittedBy AdmittedBy,
    double? KeywordCoverage = null);
