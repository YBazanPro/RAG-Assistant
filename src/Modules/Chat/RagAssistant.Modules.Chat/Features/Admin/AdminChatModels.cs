using System.Text.Json.Serialization;

namespace RagAssistant.Modules.Chat.Features.Admin;

/// <summary>One request (one trace row) in the maintainers' list (spec 017). The question is cut; there is no answer text.</summary>
internal sealed record RequestListItem(
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

/// <summary>The inspector's view of one trace (spec 017). Holds neither answer text nor passage text.</summary>
internal sealed record RequestDetail(
    Guid Id,
    Guid ConversationId,
    Guid UserMessageId,
    Guid AssistantMessageId,
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

internal sealed record RequestQuestion(
    string Text,
    string? Language,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ScopedDocument> Documents,
    IReadOnlyList<Guid> UnavailableDocumentIds);

/// <param name="Title">Only for a document that is currently shared; null otherwise, whatever the trace stored.</param>
internal sealed record ScopedDocument(Guid Id, string? Title);

internal sealed record RequestRewrite(
    bool Used, string? RetrievalQuery, string? FallbackReason, int? Ms, long? InputTokens, long? OutputTokens, string? PromptVersion);

internal sealed record RequestRetrieval(
    string? EvidenceOutcome,
    double? MinScore,
    double? ConfidentScore,
    double? DuplicateOverlap,
    double? BestScore,
    string? EmbeddingModel,
    int? Ms,
    IReadOnlyList<RequestPassage> Passages);

/// <param name="DocumentTitle">Only for a document that is currently shared; null otherwise.</param>
internal sealed record RequestPassage(
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

internal sealed record RequestGeneration(
    string? ChatModel,
    string? PromptVersion,
    long? InputTokens,
    long? OutputTokens,
    bool SentinelHit,
    int StrippedMarkerCount,
    int? FirstTokenMs,
    int TotalMs);

internal sealed record RequestResult(string FinalStatus, string? RefusalReason, string? ErrorCode);

internal sealed record RequestFeedback(string? Rating, IReadOnlyList<FeedbackReport> Reports);

internal sealed record FeedbackReport(string Category, string? Comment, DateTimeOffset CreatedAt);

/// <param name="Kind"><c>rating</c> or <c>report</c>.</param>
/// <param name="RequestId">The latest trace of the answer; null when the answer has none.</param>
internal sealed record FeedbackListItem(
    string Kind, string? Category, string? Comment, DateTimeOffset CreatedAt, Guid? RequestId, string? QuestionText, string? OwnerSub);

internal sealed record Percentiles(
    [property: JsonPropertyName("p50")] double? P50,
    [property: JsonPropertyName("p95")] double? P95);

internal sealed record ErrorCount(string Stage, string ErrorCode, int Count);

internal sealed record ChatSummary(
    string Window,
    DateTimeOffset Since,
    int Requests,
    IReadOnlyDictionary<string, int> ByStatus,
    double RefusalRate,
    IReadOnlyDictionary<string, int> ByEvidenceOutcome,
    Percentiles FirstTokenMs,
    Percentiles TotalMs,
    IReadOnlyList<ErrorCount> Errors,
    int NegativeFeedback);

internal sealed record ChatConfiguration(
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
