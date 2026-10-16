using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Chat;

/// <summary>Typed client over the BFF's <see cref="HttpClient"/>, resolved lazily so the page can prerender (see <see cref="DocumentsApi"/>).</summary>
public sealed class ChatApi(Func<HttpClient> httpClient) : IChatApi
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ApiResult<ConversationSummary>> CreateConversationAsync(CancellationToken cancellationToken) =>
        SendAsync<ConversationSummary>(() => httpClient().PostAsync("api/chat/conversations", content: null, cancellationToken), cancellationToken);

    public Task<ApiResult<ConversationPage>> ListConversationsAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        SendAsync<ConversationPage>(
            () => httpClient().GetAsync(
                $"api/chat/conversations?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<ConversationDetail>> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        SendAsync<ConversationDetail>(() => httpClient().GetAsync($"api/chat/conversations/{conversationId}", cancellationToken), cancellationToken);

    private static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            if (!response.IsSuccessStatusCode)
            {
                return ApiResult.Fail<T>(await DocumentsApi.ToErrorAsync(response, cancellationToken));
            }

            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
            return value is null
                ? ApiResult.Fail<T>(new ApiError((int)response.StatusCode, "response.empty", "The server sent an empty response.", DocumentsApi.NoFields))
                : ApiResult.Ok(value);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<T>(new ApiError(0, "network", "The server could not be reached.", DocumentsApi.NoFields));
        }
        catch (JsonException)
        {
            return ApiResult.Fail<T>(new ApiError(0, "response.malformed", "The server sent an unreadable response.", DocumentsApi.NoFields));
        }
    }
}
