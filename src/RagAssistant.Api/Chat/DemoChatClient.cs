using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace RagAssistant.Api.Chat;

/// <summary>
/// The deterministic demo mode of ADR 0003 (<c>Chat:Provider = Demo</c>): no model download, real retrieval. It answers with the
/// first sentence of the top one or two numbered passages of the prompt, cites them as [1] and [2], streams word by word and
/// never declines. It reads the passage layout the Chat module's prompt builder writes (inside each &lt;passage&gt; block, the
/// "[n] title" line, then the text; <c>DemoChatClientTests</c> pins it).
/// For demos and UI work only, never for evaluation; tests use their own scripted fakes.
/// </summary>
internal sealed partial class DemoChatClient(TimeProvider clock) : IChatClient
{
    public const string ModelId = "demo";

    private static readonly TimeSpan WordDelay = TimeSpan.FromMilliseconds(30);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // The follow-up rewrite (spec 014) is the only structured request: the demo returns the question unchanged.
        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            const string Marker = "Current question: ";
            var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            var at = user.LastIndexOf(Marker, StringComparison.Ordinal);
            var question = at < 0 ? user : user[(at + Marker.Length)..];
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, System.Text.Json.JsonSerializer.Serialize(new { standaloneQuestion = question })))
            {
                ModelId = ModelId,
            };
        }

        var text = new StringBuilder();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            text.Append(update.Text);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString())) { ModelId = ModelId };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        foreach (var word in Answer(prompt).Split(' '))
        {
            await Task.Delay(WordDelay, clock, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ") { ModelId = ModelId };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("demo", defaultModelId: ModelId) : null;

    public void Dispose()
    {
    }

    internal static string Answer(string prompt)
    {
        var sentences = Passage().Matches(prompt)
            .Where(m => m.Groups["n"].Value is "1" or "2")
            .Select(m => $"{FirstSentence(m.Groups["text"].Value)} [{m.Groups["n"].Value}]")
            .ToList();
        return sentences.Count > 0 ? string.Join(' ', sentences) : "The passages found do not say more about this.";
    }

    private static string FirstSentence(string text)
    {
        var end = SentenceEnd().Match(text);
        var sentence = end.Success ? text[..(end.Index + 1)] : text;
        return sentence.Length <= 300 ? sentence.Trim() : sentence[..300].Trim() + "…";
    }

    [GeneratedRegex(@"^\[(?<n>\d+)\] [^\n]*\n(?<text>[^\n]+)", RegexOptions.Multiline)]
    private static partial Regex Passage();

    [GeneratedRegex(@"[.!?](\s|$)")]
    private static partial Regex SentenceEnd();
}
