using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Chat;

/// <summary>
/// Opens the answer SSE streams (spec 012). A non-2xx answer is returned as a failure before any event, never thrown.
/// Once open, a dropped connection surfaces as an exception from the enumeration; cancelling the token aborts the request.
/// </summary>
public interface IAnswerEventSource
{
    Task<ApiResult<IAsyncEnumerable<AnswerEvent>>> AskAsync(Guid conversationId, string question, CancellationToken cancellationToken);

    Task<ApiResult<IAsyncEnumerable<AnswerEvent>>> RetryAsync(Guid conversationId, Guid assistantMessageId, CancellationToken cancellationToken);
}
