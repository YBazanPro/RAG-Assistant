namespace RagAssistant.Modules.Evaluation.Domain;

/// <summary>
/// One harness run (spec 018) uploaded for maintainers (spec 020). The report's own run id is the key, so uploading it again replaces
/// it. The JSON columns hold the report's sections as the harness wrote them; the columns beside them are what lists filter and show.
/// </summary>
internal sealed class EvaluationRun
{
    public required string Id { get; set; }

    public int SchemaVersion { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? ChatModel { get; set; }

    public string? EmbeddingModel { get; set; }

    /// <summary>The model of the latest judged upload; null until the run is judged.</summary>
    public string? JudgeModel { get; set; }

    /// <summary><c>vector</c> or <c>hybrid</c>; null for reports from before spec 019.</summary>
    public string? RetrievalMode { get; set; }

    public bool Rerank { get; set; }

    public required string DatasetHash { get; set; }

    public required string CorpusHash { get; set; }

    public string? GitCommit { get; set; }

    /// <summary>False when the harness found the run unusable (its <c>invalidReason</c> is in <see cref="Metrics"/>).</summary>
    public bool Valid { get; set; }

    public int CaseCount { get; set; }

    /// <summary>The report's <c>configuration</c> object.</summary>
    public required string Configuration { get; set; }

    /// <summary><c>overall</c>, <c>byCategory</c>, <c>byQuestionLanguage</c>, <c>brokenCases</c>, <c>variants</c> and the validity fields.</summary>
    public required string Metrics { get; set; }

    /// <summary>The judge's aggregates and per-case scores; null until judged.</summary>
    public string? Judge { get; set; }

    public required string Cases { get; set; }

    /// <summary>The uploader's <c>sub</c>.</summary>
    public required string UploadedBy { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
