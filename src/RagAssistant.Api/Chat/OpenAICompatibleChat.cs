using System.ClientModel;
using System.ClientModel.Primitives;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace RagAssistant.Api.Chat;

/// <summary>
/// The optional hosted chat provider (spec 018, ADR 0003 addendum): any OpenAI-compatible endpoint, OpenRouter by default.
/// Chat only; embeddings stay on the local model. Opt-in because questions and retrieved passages leave the machine.
/// </summary>
internal static class OpenAICompatibleChat
{
    public const string ProviderName = "OpenAICompatible";
    public const string Section = "Chat:OpenAICompatible";
    private const string HttpClientName = "openai-compatible-chat";

    public static ChatClientBuilder AddOpenAICompatibleChat(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAICompatibleOptions>()
            .Bind(configuration.GetSection(Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHttpClient(HttpClientName);

        return services.AddChatClient(provider =>
        {
            var options = provider.GetRequiredService<IOptions<OpenAICompatibleOptions>>().Value;
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return Create(options, http);
        });
    }

    /// <summary>
    /// The SDK's own retries are off: spec 012 has no automatic model retries (the user retries), and the HTTP resilience
    /// handler of ServiceDefaults already sits under the client. The deadlines that count are the Chat module's.
    /// </summary>
    internal static IChatClient Create(OpenAICompatibleOptions options, HttpClient http) =>
        new OpenAIClient(
                new ApiKeyCredential(options.ApiKey!),
                new OpenAIClientOptions
                {
                    Endpoint = options.Endpoint,
                    Transport = new HttpClientPipelineTransport(http),
                    RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                    NetworkTimeout = TimeSpan.FromMinutes(5),
                })
            .GetChatClient(options.Model!)
            .AsIChatClient();
}

internal sealed class OpenAICompatibleOptions
{
    [Required]
    public Uri Endpoint { get; set; } = new("https://openrouter.ai/api/v1");

    /// <summary>The provider's model id, e.g. <c>mistralai/mistral-small-3.2-24b-instruct</c> on OpenRouter.</summary>
    [Required]
    public string? Model { get; set; }

    /// <summary>From user secrets or the AppHost's secret parameter, never from a committed file.</summary>
    [Required]
    public string? ApiKey { get; set; }
}
