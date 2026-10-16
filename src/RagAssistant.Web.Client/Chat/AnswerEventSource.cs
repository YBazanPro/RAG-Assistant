using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Chat;

public sealed class AnswerEventSource(Func<HttpClient> httpClient) : IAnswerEventSource
{
    public Task<ApiResult<IAsyncEnumerable<AnswerEvent>>> AskAsync(Guid conversationId, string question, CancellationToken cancellationToken) =>
        OpenAsync(
            $"api/chat/conversations/{conversationId}/messages",
            JsonContent.Create(new { question }, options: ChatApi.Json),
            cancellationToken);

    public Task<ApiResult<IAsyncEnumerable<AnswerEvent>>> RetryAsync(Guid conversationId, Guid assistantMessageId, CancellationToken cancellationToken) =>
        OpenAsync($"api/chat/conversations/{conversationId}/messages/{assistantMessageId}/retry", content: null, cancellationToken);

    private async Task<ApiResult<IAsyncEnumerable<AnswerEvent>>> OpenAsync(string url, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Without this the browser's fetch buffers the body and no event arrives before the stream ends.
        request.SetBrowserResponseStreamingEnabled(true);

        HttpResponseMessage response;
        try
        {
            response = await httpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            request.Dispose();
            return ApiResult.Fail<IAsyncEnumerable<AnswerEvent>>(new ApiError(0, "network", "The server could not be reached.", DocumentsApi.NoFields));
        }

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                return ApiResult.Fail<IAsyncEnumerable<AnswerEvent>>(await DocumentsApi.ToErrorAsync(response, cancellationToken));
            }
            finally
            {
                response.Dispose();
                request.Dispose();
            }
        }

        return ApiResult.Ok(ReadAsync(request, response, cancellationToken));
    }

    // The enumeration owns the request and response, so they live exactly as long as the stream is read.
    private static async IAsyncEnumerable<AnswerEvent> ReadAsync(
        HttpRequestMessage request, HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (request)
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                if (AnswerEventParser.Parse(item.EventType, item.Data) is { } parsed)
                {
                    yield return parsed;
                }
            }
        }
    }
}

public static class AnswerEventParser
{
    /// <summary>Null for heartbeats, unknown types and data that does not parse: the stream must survive additions to the contract.</summary>
    public static AnswerEvent? Parse(string type, string data)
    {
        try
        {
            return type switch
            {
                "question.accepted" => JsonSerializer.Deserialize<QuestionAccepted>(data, ChatApi.Json),
                "retrieval.started" => JsonSerializer.Deserialize<RetrievalStarted>(data, ChatApi.Json),
                "retrieval.completed" => JsonSerializer.Deserialize<RetrievalCompleted>(data, ChatApi.Json),
                "answer.delta" => JsonSerializer.Deserialize<AnswerDelta>(data, ChatApi.Json),
                "answer.completed" => JsonSerializer.Deserialize<AnswerCompleted>(data, ChatApi.Json),
                "error" => JsonSerializer.Deserialize<AnswerError>(data, ChatApi.Json),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
