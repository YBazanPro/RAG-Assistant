namespace RagAssistant.Web.Client.Chat;

// The client owns its copy of the chat API's JSON (spec 012); members the client does not read are simply absent.

public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);

public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>A cited passage as snapshotted when the answer was written; the preview renders from this alone.</summary>
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
    string Text);

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
    IReadOnlyList<Citation>? Citations);

public sealed record ConversationDetail(
    Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, IReadOnlyList<ChatMessage> Messages);

/// <summary>A source named by <c>retrieval.completed</c>; it carries no passage text.</summary>
public sealed record AnswerSource(
    int Marker,
    Guid PassageId,
    Guid DocumentId,
    string DocumentTitle,
    int VersionNumber,
    IReadOnlyList<string> SectionPath,
    int? PageStart,
    int? PageEnd);

/// <summary>One event of the answer stream (spec 012). Heartbeats and unknown types never reach the consumer.</summary>
public abstract record AnswerEvent;

public sealed record QuestionAccepted(Guid ConversationId, Guid UserMessageId, Guid AssistantMessageId) : AnswerEvent;

public sealed record RetrievalCompleted(string Outcome, IReadOnlyList<AnswerSource> Sources) : AnswerEvent;

public sealed record AnswerDelta(string Text) : AnswerEvent;

public sealed record AnswerCompleted(string Status, string? RefusalReason, IReadOnlyList<Citation> Citations) : AnswerEvent;

public sealed record AnswerError(string Code, string Message, bool Retryable) : AnswerEvent;
