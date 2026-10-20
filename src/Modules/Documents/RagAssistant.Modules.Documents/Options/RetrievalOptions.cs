using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class RetrievalOptions
{
    public const string Section = "Documents:Retrieval";

    [Range(1, int.MaxValue)]
    public int DefaultTopK { get; set; } = 8;

    [Range(1, int.MaxValue)]
    public int MaxTopK { get; set; } = 20;

    /// <summary>The candidate pool is topK × this, capped at <see cref="MaxCandidates"/>; leaves room for the floor and duplicates.</summary>
    [Range(1, int.MaxValue)]
    public int CandidateMultiplier { get; set; } = 4;

    [Range(1, int.MaxValue)]
    public int MaxCandidates { get; set; } = 200;

    /// <summary>Passages below this cosine similarity never reach a caller. Provisional until Phase 5 (spec 011, calibration).</summary>
    [Range(0.0, 1.0)]
    public double MinScore { get; set; } = 0.5;

    /// <summary>A best score below this makes the evidence low-confidence. Provisional until Phase 5.</summary>
    [Range(0.0, 1.0)]
    public double ConfidentScore { get; set; } = 0.6;

    /// <summary>Word 3-gram Jaccard at or above which a lower-ranked passage counts as a duplicate. Provisional until Phase 5.</summary>
    [Range(double.Epsilon, 1.0)]
    public double DuplicateOverlap { get; set; } = 0.8;

    public TimeSpan QueryEmbeddingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan QueryEmbeddingCacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary><c>Documents:Retrieval:Hybrid</c> (spec 019).</summary>
    public HybridOptions Hybrid { get; set; } = new();

    /// <summary><c>Documents:Retrieval:Reranking</c> (spec 019).</summary>
    public RerankingOptions Reranking { get; set; } = new();

    public bool IsConsistent =>
        DefaultTopK <= MaxTopK
        && MaxCandidates >= MaxTopK
        && ConfidentScore >= MinScore
        && QueryEmbeddingTimeout > TimeSpan.Zero
        && QueryEmbeddingCacheDuration > TimeSpan.Zero
        && Hybrid.RrfK >= 1
        && Reranking.Candidates is >= 1 and <= RerankingOptions.MaxCandidates
        && Reranking.MinScore is >= 0 and <= 1
        && Reranking.ConfidentScore >= Reranking.MinScore && Reranking.ConfidentScore <= 1
        && Reranking.Timeout > TimeSpan.Zero;

    public int PoolSize(int topK) => (int)Math.Min((long)topK * CandidateMultiplier, MaxCandidates);
}

/// <summary>Keyword search fused with vector search (spec 019). Off until the harness shows a gain (spec 019, Calibration).</summary>
internal sealed class HybridOptions
{
    public bool Enabled { get; set; }

    /// <summary>The k of reciprocal rank fusion: score = Σ 1 / (k + rank).</summary>
    public int RrfK { get; set; } = 60;
}

/// <summary>A cross-encoder reorders and gates the best candidates (spec 019). Off until the harness shows a gain.</summary>
internal sealed class RerankingOptions
{
    /// <summary>One call to the reranker: the AppHost starts Text Embeddings Inference with <c>--max-client-batch-size 100</c> (ADR 0010).</summary>
    public const int MaxCandidates = 100;

    public bool Enabled { get; set; }

    /// <summary>How many of the best fused (or vector) candidates are sent to the reranker, in one call.</summary>
    public int Candidates { get; set; } = 30;

    /// <summary>The floor on the rerank score when reranking was applied; provisional until the sweep (spec 019, Calibration).</summary>
    public double MinScore { get; set; } = 0.1;

    /// <summary>A best rerank score below this makes the evidence low-confidence; provisional until the sweep.</summary>
    public double ConfidentScore { get; set; } = 0.5;

    /// <summary>Past this, retrieval goes on without reranking (spec 019, fallback).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}
