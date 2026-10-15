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

    public bool IsConsistent =>
        DefaultTopK <= MaxTopK
        && MaxCandidates >= MaxTopK
        && ConfidentScore >= MinScore
        && QueryEmbeddingTimeout > TimeSpan.Zero
        && QueryEmbeddingCacheDuration > TimeSpan.Zero;

    public int PoolSize(int topK) => (int)Math.Min((long)topK * CandidateMultiplier, MaxCandidates);
}
