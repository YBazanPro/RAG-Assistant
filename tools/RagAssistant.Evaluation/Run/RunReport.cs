namespace RagAssistant.Evaluation.Run;

/// <summary>
/// One run (spec 018), written as <c>report.json</c>: everything needed to repeat it and to compare it with another run. Answer
/// metrics are null when they were not measured (demo mode) or do not apply to a case.
/// </summary>
internal sealed record RunReport(
    int SchemaVersion,
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Valid,
    string? InvalidReason,
    bool AnswerMetricsMeasured,
    RunConfiguration Configuration,
    IReadOnlyList<BrokenCase> BrokenCases,
    Aggregate Overall,
    IReadOnlyDictionary<string, Aggregate> ByCategory,
    IReadOnlyDictionary<string, Aggregate> ByQuestionLanguage,
    IReadOnlyList<CaseResult> Cases,
    IReadOnlyDictionary<Guid, CorpusPassage>? Passages = null,
    IReadOnlyList<VariantResult>? Variants = null)
{
    public const int CurrentSchemaVersion = 1;
}

/// <param name="Scope">The tag every harness conversation was scoped to.</param>
internal sealed record RunConfiguration(
    string ChatProvider,
    string? ChatModel,
    string? EmbeddingModel,
    string? PromptVersion,
    string? RewritePromptVersion,
    int TopK,
    double MinScore,
    double ConfidentScore,
    double DuplicateOverlap,
    int ChunkMaxTokens,
    int ChunkOverlapTokens,
    string CorpusHash,
    string DatasetHash,
    string Scope,
    string? GitCommit,
    string ApiBaseUrl,
    string? RetrievalMode = null,
    bool? Reranking = null,
    int? RrfK = null,
    int? RerankCandidates = null,
    string? RerankerModel = null,
    double? RerankMinScore = null,
    double? RerankConfidentScore = null);

/// <summary>A passage of the seeded corpus; the sweep needs the text to redo near-duplicate reduction offline.</summary>
internal sealed record CorpusPassage(string Document, string Text);

/// <summary>A case whose expected evidence no longer exists in the corpus: excluded and counted, never passed silently (AC-25).</summary>
internal sealed record BrokenCase(string Id, string Reason);

/// <param name="Rank">1-based, in the order retrieval returned the passages.</param>
/// <param name="Matches">The index of the expected evidence item this passage matches, or null.</param>
internal sealed record RetrievedPassage(int Rank, Guid PassageId, string? Document, double Score, string? Language, int? Matches);

/// <summary>A candidate of the raw pool (spec 018, threshold sweep), in rank order.</summary>
/// <param name="Matches">The index of the expected evidence item this passage matches, or null.</param>
internal sealed record PoolCandidate(Guid PassageId, string? Document, double Score, string? Dropped, int? Matches);

internal sealed record CaseMetrics(
    double? RecallAt1,
    double? RecallAt3,
    double? RecallAt5,
    double? RecallAtTopK,
    double? ReciprocalRank,
    bool? BehaviourCorrect,
    double? CitationRecall,
    double? CitationPrecision,
    string? AnswerLanguageDetected,
    bool? AnswerLanguageCorrect,
    bool? InjectionResisted = null);

internal sealed record CaseResult(
    string Id,
    string Category,
    string QuestionLanguage,
    IReadOnlyList<string> Turns,
    string ExpectedBehaviour,
    string ExpectedAnswerLanguage,
    int ExpectedEvidence,
    string Status,
    string? RefusalReason,
    string? Error,
    string? RetrievalQuery,
    string? EvidenceOutcome,
    string AnswerText,
    IReadOnlyList<RetrievedPassage> Retrieved,
    IReadOnlyList<Guid> Cited,
    IReadOnlyList<PoolCandidate> Pool,
    string? ChatModel,
    string? PromptVersion,
    string? QuestionLanguageDetected,
    string? AnswerLanguageInstructed,
    int? FirstTokenMs,
    int? TotalMs,
    CaseMetrics Metrics,
    string? RetrievalMode = null);

/// <summary>Means over the cases a metric applies to; <see cref="Cases"/> counts every case in the group.</summary>
internal sealed record Aggregate(
    int Cases,
    int Errors,
    double? RecallAt1,
    double? RecallAt3,
    double? RecallAt5,
    double? RecallAtTopK,
    double? Mrr,
    double? BehaviourCorrectness,
    double? RefusalCorrectness,
    double? CitationRecall,
    double? CitationPrecision,
    double? AnswerLanguageCorrectness,
    double? MedianFirstTokenMs,
    double? InjectionResistance = null);
