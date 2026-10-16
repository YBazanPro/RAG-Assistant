namespace RagAssistant.Web.Client.Chat;

/// <summary>What an answer looks like right now, whether it comes from the server (history) or from a stream in progress.</summary>
/// <param name="Status">
/// <c>streaming</c>, <c>lost</c> (the connection dropped, the server state is being fetched), or the API's
/// <c>pending</c>, <c>complete</c>, <c>partial</c>, <c>refused</c>, <c>failed</c>, <c>cancelled</c>.
/// </param>
/// <param name="Sources">What a <c>[n]</c> marker in the text resolves against.</param>
/// <param name="Cited">The sources the answer cites, in marker order; the <see cref="CitationList"/>.</param>
public sealed record AnswerView(
    string Status,
    string Text,
    string? RefusalReason,
    string? ErrorMessage,
    bool Retryable,
    string? StageLine,
    IReadOnlyList<Citation> Sources,
    IReadOnlyList<Citation> Cited,
    DateTimeOffset? CompletedAt,
    Guid? MessageId)
{
    public bool IsStreaming => Status == "streaming";

    public static AnswerView From(ChatMessage message)
    {
        var cited = (message.Citations ?? []).OrderBy(c => c.Marker).ToArray();
        return new AnswerView(
            message.Status ?? "pending",
            message.Text,
            message.RefusalReason,
            message.Status == "failed" ? ChatTexts.ForError(message.ErrorCode) : null,
            message.Retryable ?? false,
            null,
            cited,
            cited,
            message.CompletedAt,
            message.Id);
    }
}

/// <summary>Wording the API does not send with a stored answer; the same texts as the stream's <c>error</c> events (spec 012).</summary>
public static class ChatTexts
{
    public static string ForError(string? code) => code switch
    {
        "retrieval.embedding_unavailable" => "The search service is unavailable right now. Try again in a moment.",
        "chat.model_unavailable" => "The answer model is unavailable right now. Try again in a moment.",
        "chat.model_timeout" => "The answer model took too long to respond. Try again.",
        "chat.interrupted" => "The answer was interrupted. Try again.",
        _ => "Something went wrong while writing the answer.",
    };
}
