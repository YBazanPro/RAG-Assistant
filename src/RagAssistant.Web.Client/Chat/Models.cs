namespace RagAssistant.Web.Client.Chat;

// The client owns its copy of the chat API's JSON (spec 012); members the client does not read are simply absent.

public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);

public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// A cited passage as snapshotted when the answer was written; the preview renders from this alone. While an answer streams,
/// the sources come from <c>retrieval.completed</c>, which has no passage text: <see cref="Text"/> is then null.
/// </summary>
public sealed record Citation(
    int Marker,
    Guid PassageId,
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    string DocumentTitle,
    IReadOnlyList<string> SectionPath,
    int? PageStart,
    int? PageEnd,
    string? Text,
    bool DocumentReadable = true)
{
    public static Citation FromSource(AnswerSource s) =>
        new(s.Marker, s.PassageId, s.DocumentId, Guid.Empty, s.VersionNumber, s.DocumentTitle, s.SectionPath, s.PageStart, s.PageEnd, null);
}

/// <param name="Role"><c>user</c> or <c>assistant</c>.</param>
/// <param name="Status">Assistant messages only: pending, complete, partial, refused, failed or cancelled.</param>
public sealed record ChatMessage(
    Guid Id,
    string Role,
    int Sequence,
    string Text,
    DateTimeOffset CreatedAt,
    Guid? ReplyToMessageId,
    string? Status,
    string? RefusalReason,
    string? ErrorCode,
    bool? Retryable,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<Citation>? Citations,
    string? RetrievalQuery = null,
    string? Rating = null,
    int? ReportCount = null);

public sealed record ConversationDetail(
    Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, IReadOnlyList<ChatMessage> Messages, ConversationScope? Scope = null);

/// <summary>The documents and tags a conversation searches (spec 015); empty means the whole corpus.</summary>
public sealed record ConversationScope(
    IReadOnlyList<Guid> DocumentIds, IReadOnlyList<string> Tags, IReadOnlyList<Guid> UnavailableDocumentIds, IReadOnlyList<ScopeDocument> Documents)
{
    public static ConversationScope Empty { get; } = new([], [], [], []);

    public bool IsEmpty => DocumentIds.Count == 0 && Tags.Count == 0;
}

/// <summary>A source named by <c>retrieval.completed</c>; it carries no passage text.</summary>
public sealed record AnswerSource(
    int Marker,
    Guid PassageId,
    Guid DocumentId,
    string DocumentTitle,
    int VersionNumber,
    IReadOnlyList<string> SectionPath,
    int? PageStart,
    int? PageEnd,
    double Score = 0);

/// <summary>The filters a question was searched under (spec 015).</summary>
public sealed record ScopeInfo(string? Language, IReadOnlyList<string> Tags, IReadOnlyList<Guid> DocumentIds);

/// <summary>One event of the answer stream (spec 012). Heartbeats and unknown types never reach the consumer.</summary>
public abstract record AnswerEvent;

public sealed record QuestionAccepted(Guid ConversationId, Guid UserMessageId, Guid AssistantMessageId) : AnswerEvent;

public sealed record RetrievalCompleted(
    string Outcome,
    IReadOnlyList<AnswerSource> Sources,
    ScopeInfo? Scope = null,
    IReadOnlyList<Guid>? LeftOut = null,
    double? MinScore = null,
    double? ConfidentScore = null,
    int? RetrievalMs = null,
    double? BestScore = null) : AnswerEvent;

/// <summary>What retrieval searches for; differs from the question for a follow-up (spec 014).</summary>
public sealed record RetrievalStarted(string Query) : AnswerEvent;

public sealed record AnswerDelta(string Text) : AnswerEvent;

public sealed record AnswerCompleted(string Status, string? RefusalReason, IReadOnlyList<Citation> Citations) : AnswerEvent;

public sealed record AnswerError(string Code, string Message, bool Retryable) : AnswerEvent;

/// <summary>A citation chosen in the thread: which answer, which of its markers, and the element that opened it (it gets focus back).</summary>
public readonly record struct CitationSelection(Guid MessageId, int Marker, string OpenerId);

/// <summary>The answer of calls that return no body (204).</summary>
public sealed record Done;

public sealed record RatingResult(string Rating, DateTimeOffset UpdatedAt);

public sealed record IssueReportResult(Guid Id, string Category, string? Comment, DateTimeOffset CreatedAt);

public sealed record CategoryChoice(string Value, string Text);

/// <summary>What the report dialog hands back: a category and an optional comment.</summary>
public sealed record ReportRequest(string Category, string? Comment);

/// <summary>What the scope picker hands back: the chosen documents (with the titles it saw) and tags.</summary>
public sealed record ScopeChoice(IReadOnlyList<ScopeDocument> Documents, IReadOnlyList<string> Tags);

// What the pipeline did for one answer (spec 015); the same shape the activity endpoint returns. Live answers fill it in part.

public sealed record ActivityView(string Question, RewriteStep Rewrite, ScopeStep Scope, RetrievalStep Retrieval, AnswerStep? Answer);

public sealed record RewriteStep(bool Used, string? Query, string? FallbackReason, int? Ms);

public sealed record ScopeStep(string? Language, IReadOnlyList<string> Tags, IReadOnlyList<ScopeDocument> Documents);

public sealed record ScopeDocument(Guid Id, string? Title, bool Available);

public sealed record RetrievalStep(
    string? Outcome, double? MinScore, double? ConfidentScore, int? Ms, IReadOnlyList<PassageStep> Passages, double? BestScore = null);

public sealed record PassageStep(
    int Rank,
    int Marker,
    double Score,
    bool Cited,
    Guid? DocumentId,
    string? DocumentTitle,
    int? VersionNumber,
    IReadOnlyList<string> SectionPath,
    int? PageStart,
    int? PageEnd);

public sealed record AnswerStep(
    bool ModelCalled,
    string Status,
    string? RefusalReason,
    string? ErrorCode,
    int? FirstTokenMs,
    int TotalMs,
    int StrippedMarkerCount,
    bool SentinelHit);
