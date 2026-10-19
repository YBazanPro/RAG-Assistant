using System.Text.Json;
using Microsoft.Extensions.AI;
using RagAssistant.Api.Chat;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 018 (revision 2026-10-19): the API asks Ollama not to think unless Chat:Ollama:Think is true.
public sealed class OllamaThinkingTests
{
    private sealed class Capture : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static string RequestJson(Capture inner) =>
        JsonSerializer.Serialize(inner.Options!.RawRepresentationFactory!(inner) as OllamaChatRequest);

    [Test]
    [Arguments(false, "\"think\":false")]
    [Arguments(true, "\"think\":true")]
    public async Task Streaming_setsTheThinkFlagOnTheOllamaRequest(bool think, string expected)
    {
        var inner = new Capture();
        using var client = new OllamaThinking(inner, think);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")], new ChatOptions { MaxOutputTokens = 10 }))
        {
        }

        await Assert.That(RequestJson(inner)).Contains(expected);
        await Assert.That(inner.Options!.MaxOutputTokens).IsEqualTo(10);
    }

    [Test]
    public async Task Response_keepsARawRequestTheCallerAlreadyProvided()
    {
        var inner = new Capture();
        using var client = new OllamaThinking(inner, think: false);
        var options = new ChatOptions { RawRepresentationFactory = _ => new OllamaChatRequest { Model = "caller-model" } };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], options);

        var json = RequestJson(inner);
        await Assert.That(json).Contains("\"think\":false");
        await Assert.That(json).Contains("caller-model");
        // The caller's own options are not modified.
        await Assert.That(JsonSerializer.Serialize(options.RawRepresentationFactory!(inner) as OllamaChatRequest)).DoesNotContain("\"think\":false");
    }
}
