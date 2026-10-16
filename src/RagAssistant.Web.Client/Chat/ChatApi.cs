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
        DocumentsApi.SendAsync<ConversationSummary>(() => httpClient().PostAsync("api/chat/conversations", content: null, cancellationToken), cancellationToken);

    public Task<ApiResult<ConversationPage>> ListConversationsAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        DocumentsApi.SendAsync<ConversationPage>(
            () => httpClient().GetAsync(
                $"api/chat/conversations?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<ConversationDetail>> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        DocumentsApi.SendAsync<ConversationDetail>(() => httpClient().GetAsync($"api/chat/conversations/{conversationId}", cancellationToken), cancellationToken);

    public Task<ApiResult<ActivityView>> GetActivityAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        DocumentsApi.SendAsync<ActivityView>(
            () => httpClient().GetAsync($"api/chat/conversations/{conversationId}/messages/{messageId}/activity", cancellationToken), cancellationToken);

    public Task<ApiResult<Done>> DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        SendNoBodyAsync(() => httpClient().DeleteAsync($"api/chat/conversations/{conversationId}", cancellationToken), cancellationToken);

    public Task<ApiResult<RatingResult>> SetRatingAsync(Guid conversationId, Guid messageId, string rating, CancellationToken cancellationToken) =>
        DocumentsApi.SendAsync<RatingResult>(
            () => httpClient().PutAsJsonAsync($"api/chat/conversations/{conversationId}/messages/{messageId}/rating", new { rating }, Json, cancellationToken),
            cancellationToken);

    public Task<ApiResult<Done>> ClearRatingAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        SendNoBodyAsync(() => httpClient().DeleteAsync($"api/chat/conversations/{conversationId}/messages/{messageId}/rating", cancellationToken), cancellationToken);

    public Task<ApiResult<IssueReportResult>> ReportIssueAsync(
        Guid conversationId, Guid messageId, string category, string? comment, CancellationToken cancellationToken) =>
        DocumentsApi.SendAsync<IssueReportResult>(
            () => httpClient().PostAsJsonAsync($"api/chat/conversations/{conversationId}/messages/{messageId}/reports", new { category, comment }, Json, cancellationToken),
            cancellationToken);

    private static async Task<ApiResult<Done>> SendNoBodyAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            return response.IsSuccessStatusCode
                ? ApiResult.Ok(new Done())
                : ApiResult.Fail<Done>(await DocumentsApi.ToErrorAsync(response, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<Done>(new ApiError(0, "network", "The server could not be reached.", DocumentsApi.NoFields));
        }
    }
}
