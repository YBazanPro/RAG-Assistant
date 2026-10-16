using System.Net.ServerSentEvents;
using System.Text.Json;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>The answer stream contract (spec 012): dotted event types, camelCase JSON data, no event ids (not resumable).</summary>
internal static class AnswerEvents
{
    public const string QuestionAcceptedType = "question.accepted";
    public const string RetrievalStartedType = "retrieval.started";
    public const string RetrievalCompletedType = "retrieval.completed";
    public const string AnswerDeltaType = "answer.delta";
    public const string AnswerCompletedType = "answer.completed";
    public const string ErrorType = "error";
    public const string HeartbeatType = "heartbeat";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record QuestionAccepted(Guid ConversationId, Guid UserMessageId, Guid AssistantMessageId);

    public sealed record RetrievalStarted;

    public sealed record Source(
        int Marker,
        Guid PassageId,
        Guid DocumentId,
        string DocumentTitle,
        int VersionNumber,
        IReadOnlyList<string> SectionPath,
        int? PageStart,
        int? PageEnd);

    public sealed record RetrievalCompleted(string Outcome, IReadOnlyList<Source> Sources);

    public sealed record AnswerDelta(string Text);

    public sealed record AnswerCompleted(string Status, string? RefusalReason, IReadOnlyList<CitationView> Citations);

    public sealed record StreamError(string Code, string Message, bool Retryable);

    public static SseItem<string> Item<T>(string type, T data) => new(JsonSerializer.Serialize(data, Json), type);

    public static SseItem<string> Heartbeat() => new(string.Empty, HeartbeatType);

    /// <summary>The same spelling as the diagnostic search endpoint (spec 011).</summary>
    public static string ToApi(this EvidenceOutcome outcome) => JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString());
}
