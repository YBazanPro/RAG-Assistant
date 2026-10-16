using System.Text.RegularExpressions;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>One earlier question and what its answer contributes to the rewrite: the cleaned text, or null (refused, failed, cancelled).</summary>
internal sealed record Exchange(string Question, string? Answer);

/// <summary>
/// Builds the bounded history the follow-up rewrite sees (spec 014). It is used by the rewrite only: the answer prompt never
/// gets earlier answers, so a wrong one cannot leak into a grounded answer.
/// </summary>
internal static partial class FollowUpHistory
{
    /// <param name="earlier">Messages of the conversation that come before the current question, in any order.</param>
    /// <returns>The last <paramref name="exchanges"/> exchanges, oldest first.</returns>
    public static IReadOnlyList<Exchange> Build(IEnumerable<Message> earlier, int exchanges, int maxAnswerChars)
    {
        var messages = earlier.OrderBy(m => m.Sequence).ToList();
        var answersByQuestion = messages
            .Where(m => m.Role == MessageRole.Assistant && m.ReplyToMessageId is not null)
            .ToDictionary(m => m.ReplyToMessageId!.Value);

        return [.. messages
            .Where(m => m.Role == MessageRole.User)
            .TakeLast(exchanges)
            .Select(question =>
            {
                answersByQuestion.TryGetValue(question.Id, out var answer);
                var usable = answer is { Status: AnswerStatus.Complete or AnswerStatus.Partial };
                return new Exchange(question.Text, usable ? Clean(answer!.Text, maxAnswerChars) : null);
            })];
    }

    /// <summary>Citation markers mean nothing without their passages, so they are removed before the text goes to the rewrite.</summary>
    internal static string Clean(string answer, int maxChars)
    {
        var text = Spaces().Replace(Marker().Replace(answer, string.Empty), " ").Trim();
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
    }

    [GeneratedRegex(@"[ \t]?\[\d+\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();
}
