using Microsoft.Extensions.AI;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;
using ThinkValue = OllamaSharp.Models.Chat.ThinkValue;

namespace RagAssistant.Api.Chat;

/// <summary>
/// Sets Ollama's <c>think</c> flag on every chat call (spec 018). Reasoning models (qwen3.5) think by default: on a CPU the hidden
/// reasoning used the whole output budget and delayed the first visible token past the answer deadlines, for no gain on answers
/// that must come from the given passages. <c>Chat:Ollama:Think</c> (default false) turns it back on. Models that cannot think
/// ignore the flag. Provider-specific, so it lives in the API, never in a module (ADR 0003).
/// </summary>
internal sealed class OllamaThinking(IChatClient inner, bool think) : DelegatingChatClient(inner)
{
    public const string ConfigurationKey = "Chat:Ollama:Think";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithThink(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithThink(options), cancellationToken);

    /// <summary>OllamaSharp starts from the raw request this factory returns and fills in everything else from the options.</summary>
    internal ChatOptions WithThink(ChatOptions? options)
    {
        var copy = options?.Clone() ?? new ChatOptions();
        var previous = copy.RawRepresentationFactory;
        copy.RawRepresentationFactory = client =>
        {
            var request = previous?.Invoke(client) as OllamaChatRequest ?? new OllamaChatRequest();
            request.Think = new ThinkValue(think);
            return request;
        };
        return copy;
    }
}
