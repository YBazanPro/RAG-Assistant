namespace RagAssistant.Web.Client.Admin;

// The client owns its copy of the admin API's JSON (spec 017); members it does not read are simply absent.

public sealed record RequestListItem(
    Guid Id,
    DateTimeOffset StartedAt,
    string OwnerSub,
    string QuestionText,
    string FinalStatus,
    string? RefusalReason,
    string? ErrorCode,
    string? EvidenceOutcome,
    double? BestScore,
    int TotalMs,
    int Attempt,
    bool HasNegativeFeedback);

public sealed record RequestDetail(
    Guid Id,
    Guid ConversationId,
    int Attempt,
    string OwnerSub,
    DateTimeOffset StartedAt,
    RequestQuestion Question,
    RequestRewrite Rewrite,
    RequestRetrieval Retrieval,
    RequestGeneration Generation,
    RequestResult Result,
    RequestFeedback Feedback,
    string? OtelTraceId,
    string? TraceUrl,
    bool TitlesUnavailable);

public sealed record RequestQuestion(
    string Text,
    string? Language,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ScopedDocument> Documents,
    IReadOnlyList<Guid> UnavailableDocumentIds,
    string? DetectedLanguage = null,
    string? LanguageSource = null);

public sealed record ScopedDocument(Guid Id, string? Title);

public sealed record RequestRewrite(
    bool Used, string? RetrievalQuery, string? FallbackReason, int? Ms, long? InputTokens, long? OutputTokens, string? PromptVersion);

public sealed record RequestRetrieval(
    string? EvidenceOutcome,
    double? MinScore,
    double? ConfidentScore,
    double? DuplicateOverlap,
    double? BestScore,
    string? EmbeddingModel,
    int? Ms,
    IReadOnlyList<RequestPassage> Passages);

public sealed record RequestPassage(
    int Rank,
    int Marker,
    double Score,
    bool Cited,
    Guid PassageId,
    Guid? DocumentId,
    string? DocumentTitle,
    int? VersionNumber,
    IReadOnlyList<string> SectionPath,
    int? PageStart,
    int? PageEnd);

public sealed record RequestGeneration(
    string? ChatModel,
    string? PromptVersion,
    long? InputTokens,
    long? OutputTokens,
    bool SentinelHit,
    int StrippedMarkerCount,
    int? FirstTokenMs,
    int TotalMs,
    string? AnswerLanguage = null);

public sealed record RequestResult(string FinalStatus, string? RefusalReason, string? ErrorCode);

public sealed record RequestFeedback(string? Rating, IReadOnlyList<FeedbackReport> Reports);

public sealed record FeedbackReport(string Category, string? Comment, DateTimeOffset CreatedAt);

public sealed record FeedbackListItem(
    string Kind, string? Category, string? Comment, DateTimeOffset CreatedAt, Guid? RequestId, string? QuestionText, string? OwnerSub);

public sealed record Percentiles(double? P50, double? P95);

public sealed record ErrorCount(string Stage, string ErrorCode, int Count);

public sealed record ChatSummary(
    string Window,
    int Requests,
    IReadOnlyDictionary<string, int> ByStatus,
    double RefusalRate,
    IReadOnlyDictionary<string, int> ByEvidenceOutcome,
    Percentiles FirstTokenMs,
    Percentiles TotalMs,
    IReadOnlyList<ErrorCount> Errors,
    int NegativeFeedback);

public sealed record ChatConfiguration(
    string Provider,
    string? ChatModel,
    string PromptVersion,
    string RewritePromptVersion,
    int TopK,
    int HistoryExchanges,
    int MaxAnswerChars,
    int MaxQuestionLength,
    int MaxMessagesPerConversation,
    int MaxReportsPerAnswer,
    int MaxOutputTokens,
    double FirstTokenTimeoutSeconds,
    double TotalTimeoutSeconds,
    double RewriteTimeoutSeconds,
    bool TraceLinkConfigured);

public sealed record DocumentCounts(int ActiveShared, int ActivePrivate, int ArchivedShared, int ArchivedPrivate);

public sealed record VersionCounts(int Queued, int Processing, int Indexed, int Failed, int Superseded);

public sealed record DocumentsHealth(
    DocumentCounts Documents,
    VersionCounts Versions,
    int QueuedVersions,
    double? OldestPendingSeconds,
    int StaleShared,
    int StaleAfterDays);

public sealed record FailureItem(
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

public sealed record PrivateDocumentItem(
    Guid DocumentId,
    string Title,
    string OwnerSub,
    string Status,
    string? LatestVersionStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DocumentsConfiguration(
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
