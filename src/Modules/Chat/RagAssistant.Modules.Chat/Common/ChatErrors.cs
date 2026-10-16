using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Common;

internal static class ChatErrors
{
    public const string EmbeddingUnavailableCode = "retrieval.embedding_unavailable";
    public const string ModelUnavailableCode = "chat.model_unavailable";
    public const string ModelTimeoutCode = "chat.model_timeout";
    public const string InterruptedCode = "chat.interrupted";
    public const string InternalCode = "chat.internal";

    public static readonly Error ConversationNotFound =
        Error.NotFound("chat.conversation_not_found", "The conversation was not found.");

    public static readonly Error MessageNotFound = Error.NotFound("chat.message_not_found", "The message was not found.");

    public static readonly Error ActivityNotFound = Error.NotFound("chat.activity_not_found", "There is no activity for this answer.");

    public static readonly Error AnswerInProgress =
        Error.Conflict("chat.answer_in_progress", "An answer is already being written in this conversation. Wait for it to finish.");

    public static readonly Error ConversationFull =
        Error.Conflict("chat.conversation_full", "This conversation is full. Start a new conversation.");

    public static readonly Error RetryNotAllowed =
        Error.Conflict("chat.retry_not_allowed", "Only the last answer can be retried, and only when it failed or was cancelled.");

    public static readonly Error FeedbackNotAllowed =
        Error.Conflict("chat.feedback_not_allowed", "Feedback can only be given on a finished answer.");

    public static readonly Error ReportLimit =
        Error.Conflict("chat.report_limit", "This answer has reached the maximum number of issue reports.");

    /// <summary>Messages of the errors sent as <c>error</c> events, by code.</summary>
    public static string StreamMessage(string code) => code switch
    {
        EmbeddingUnavailableCode => "The search service is unavailable right now. Try again in a moment.",
        ModelUnavailableCode => "The answer model is unavailable right now. Try again in a moment.",
        ModelTimeoutCode => "The answer model took too long to respond. Try again.",
        InterruptedCode => "The answer was interrupted. Try again.",
        _ => "Something went wrong while writing the answer.",
    };

    /// <summary>Only <c>chat.internal</c> is not worth retrying.</summary>
    public static bool IsRetryable(string code) => code is not InternalCode;
}
