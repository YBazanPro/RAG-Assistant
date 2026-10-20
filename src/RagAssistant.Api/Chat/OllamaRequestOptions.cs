using Microsoft.Extensions.AI;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;
using OllamaRequestOptionsModel = OllamaSharp.Models.RequestOptions;
using ThinkValue = OllamaSharp.Models.Chat.ThinkValue;

namespace RagAssistant.Api.Chat;

/// <summary>
/// Sets the Ollama-only request options on every chat call. Provider-specific, so it lives in the API, never in a module (ADR 0003).
/// <list type="bullet">
/// <item><c>think</c> (spec 018): reasoning models (qwen3.5) think by default; on a CPU the hidden reasoning used the whole output
/// budget and delayed the first visible token past the answer deadlines, for no gain on answers that must come from the given
/// passages. <c>Chat:Ollama:Think</c> (default false) turns it back on. Models that cannot think ignore the flag.</item>
/// <item><c>num_ctx</c> (spec 021): the context window the Chat module budgets the prompt for (<c>Chat:Generation:ContextWindowTokens</c>).
/// Without it Ollama uses its own default and cuts a longer prompt silently, rules first.</item>
/// </list>
/// </summary>
internal sealed class OllamaRequestOptions(IChatClient inner, bool think, int contextWindowTokens) : DelegatingChatClient(inner)
{
    public const string ThinkConfigurationKey = "Chat:Ollama:Think";

    /// <summary>The Chat module's setting; read here too so the window Ollama gets is the one the prompt was budgeted for.</summary>
    public const string ContextWindowConfigurationKey = "Chat:Generation:ContextWindowTokens";

    /// <summary>The Chat module's default (<c>GenerationOptions.ContextWindowTokens</c>).</summary>
    public const int DefaultContextWindowTokens = 8192;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithOllamaOptions(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithOllamaOptions(options), cancellationToken);

    /// <summary>OllamaSharp starts from the raw request this factory returns and fills in everything else from the options.</summary>
    internal ChatOptions WithOllamaOptions(ChatOptions? options)
    {
        var copy = options?.Clone() ?? new ChatOptions();
        var previous = copy.RawRepresentationFactory;
        copy.RawRepresentationFactory = client =>
        {
            var request = previous?.Invoke(client) as OllamaChatRequest ?? new OllamaChatRequest();
            request.Think = new ThinkValue(think);
            request.Options ??= new OllamaRequestOptionsModel();
            request.Options.NumCtx = contextWindowTokens;
            return request;
        };
        return copy;
    }
}
