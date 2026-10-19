using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using RagAssistant.Api.Chat;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 018 (second revision): the optional OpenAI-compatible chat provider. No real endpoint is called (CLAUDE.md).
public sealed class OpenAICompatibleChatTests
{
    private sealed class StubEndpoint(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), content));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private const string Completion = """
        {"id":"c1","object":"chat.completion","created":1,"model":"mistralai/mistral-small-3.2",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Bonjour [1]."},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}
        """;

    private static OpenAICompatibleOptions Options() =>
        new() { Endpoint = new Uri("https://llm.example.test/api/v1"), Model = "mistralai/mistral-small-3.2", ApiKey = "test-key" };

    [Test]
    public async Task Response_callsTheConfiguredEndpointWithTheKeyAndModel()
    {
        var stub = new StubEndpoint(HttpStatusCode.OK, Completion);
        using var http = new HttpClient(stub);
        using var client = OpenAICompatibleChat.Create(Options(), http);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Bonjour ?")]);

        var request = stub.Requests.Single();
        await Assert.That(request.Uri.ToString()).IsEqualTo("https://llm.example.test/api/v1/chat/completions");
        await Assert.That(request.Authorization).IsEqualTo("Bearer test-key");
        await Assert.That(request.Body).Contains("\"model\":\"mistralai/mistral-small-3.2\"");
        await Assert.That(response.Text).IsEqualTo("Bonjour [1].");
        await Assert.That(response.ModelId).IsEqualTo("mistralai/mistral-small-3.2");
        await Assert.That(client.GetService<ChatClientMetadata>()?.DefaultModelId).IsEqualTo("mistralai/mistral-small-3.2");
    }

    [Test]
    public async Task Response_onAServerError_isNotRetriedByTheSdk()
    {
        var stub = new StubEndpoint(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"busy"}}""");
        using var http = new HttpClient(stub);
        using var client = OpenAICompatibleChat.Create(Options(), http);

        await Assert.That(async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")])).ThrowsException();

        // Spec 012: no automatic model retries; the user retries the answer.
        await Assert.That(stub.Requests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(null, "test-key")]
    [Arguments("some/model", null)]
    public async Task Options_withoutModelOrKey_failValidation(string? model, string? key)
    {
        var options = new OpenAICompatibleOptions { Model = model, ApiKey = key };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        await Assert.That(valid).IsFalse();
        await Assert.That(results.Count).IsEqualTo(1);
    }
}
