namespace RagAssistant.Modules.Documents.Features.Admin;

/// <summary>Counts only: nothing here identifies a private document (spec 017).</summary>
internal sealed record DocumentsHealth(
    DocumentCounts Documents,
    VersionCounts Versions,
    int QueuedVersions,
    double? OldestPendingSeconds,
    int StaleShared,
    int StaleAfterDays);

internal sealed record DocumentCounts(int ActiveShared, int ActivePrivate, int ArchivedShared, int ArchivedPrivate);

internal sealed record VersionCounts(int Queued, int Processing, int Indexed, int Failed, int Superseded);

/// <summary>One document whose latest version failed to index. A private document keeps its row but offers no retry.</summary>
internal sealed record FailureItem(
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    string Title,
    string Visibility,
    string OwnerSub,
    string? Stage,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? FailedAt,
    int Attempt,
    bool CanRetry);

/// <summary>The only fields a maintainer gets about someone's private document: no tags, description or passages.</summary>
internal sealed record PrivateDocumentItem(
    Guid DocumentId,
    string Title,
    string OwnerSub,
    string Status,
    string? LatestVersionStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record DocumentsConfiguration(
    string EmbeddingModel,
    int EmbeddingDimensions,
    int ChunkMaxTokens,
    int ChunkOverlapTokens,
    int ChunkMinTokens,
    int IngestionMaxConcurrentRuns,
    int IngestionEmbeddingBatchSize,
    double IngestionEmbeddingTimeoutSeconds,
    int IngestionEmbeddingMaxRetries,
    int DefaultTopK,
    int MaxTopK,
    double MinScore,
    double ConfidentScore,
    double DuplicateOverlap,
    int MaxFileSizeBytes,
    int MaxPdfSizeBytes,
    int PdfMaxPages,
    string Storage,
    int StaleAfterDays);
