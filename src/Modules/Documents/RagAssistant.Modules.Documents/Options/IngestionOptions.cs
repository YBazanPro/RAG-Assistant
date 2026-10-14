using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class IngestionOptions
{
    public const string Section = "Documents:Ingestion";

    [Range(1, 32)]
    public int MaxConcurrentRuns { get; set; } = 1;

    [Range(1, int.MaxValue)]
    public int EmbeddingBatchSize { get; set; } = 16;

    public TimeSpan EmbeddingTimeout { get; set; } = TimeSpan.FromSeconds(60);

    [Range(0, 20)]
    public int EmbeddingMaxRetries { get; set; } = 3;

    public TimeSpan EmbeddingRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ProgressPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public bool HasPositiveDurations =>
        EmbeddingTimeout > TimeSpan.Zero
        && EmbeddingRetryBaseDelay > TimeSpan.Zero
        && ProbeInterval > TimeSpan.Zero
        && ProgressPollInterval > TimeSpan.Zero
        && HeartbeatInterval > TimeSpan.Zero;
}
