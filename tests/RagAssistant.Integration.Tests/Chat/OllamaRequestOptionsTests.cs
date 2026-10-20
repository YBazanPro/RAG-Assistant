using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;
using RagAssistant.Api.Chat;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 018 (revision 2026-10-19): the API asks Ollama not to think unless Chat:Ollama:Think is true.
// Spec 021: every chat call carries num_ctx, the context window the Chat module budgets the prompt for.
public sealed class OllamaRequestOptionsTests
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

    /// <summary>Answers like Ollama's /api/chat and keeps the request body, so the test sees what OllamaSharp really sends.</summary>
    private sealed class OllamaServer : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            const string Reply =
                """{"model":"m","created_at":"2026-10-20T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply + "\n", Encoding.UTF8, "application/x-ndjson") };
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
        using var client = new OllamaRequestOptions(inner, think, 8192);

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
        using var client = new OllamaRequestOptions(inner, think: false, 8192);
        var options = new ChatOptions { RawRepresentationFactory = _ => new OllamaChatRequest { Model = "caller-model" } };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], options);

        var json = RequestJson(inner);
        await Assert.That(json).Contains("\"think\":false");
        await Assert.That(json).Contains("caller-model");
        // The caller's own options are not modified.
        await Assert.That(JsonSerializer.Serialize(options.RawRepresentationFactory!(inner) as OllamaChatRequest)).DoesNotContain("\"think\":false");
    }

    [Test]
    public async Task OllamaClient_sendsNumCtxAndThink_besideTheOptionsItMapsItself()
    {
        var server = new OllamaServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://ollama.test/") };
        using var ollama = new OllamaApiClient(http, "m");
        using var client = new OllamaRequestOptions(ollama, think: false, 6144);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")], new ChatOptions { MaxOutputTokens = 10 }))
        {
        }

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], new ChatOptions { MaxOutputTokens = 300 });

        await Assert.That(server.Bodies.Count).IsEqualTo(2);
        foreach (var body in server.Bodies)
        {
            using var json = JsonDocument.Parse(body);
            var options = json.RootElement.GetProperty("options");
            await Assert.That(options.GetProperty("num_ctx").GetInt32()).IsEqualTo(6144);
            await Assert.That(options.TryGetProperty("num_predict", out _)).IsTrue();
            await Assert.That(json.RootElement.GetProperty("think").GetBoolean()).IsFalse();
        }
    }
}
