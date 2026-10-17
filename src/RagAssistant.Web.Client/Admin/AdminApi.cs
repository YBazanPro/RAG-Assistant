using System.Globalization;
using System.Text;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Admin;

/// <summary>Typed client over the BFF's <see cref="HttpClient"/>, resolved lazily so the page can prerender (see <see cref="DocumentsApi"/>).</summary>
public sealed class AdminApi(Func<HttpClient> httpClient) : IAdminApi
{
    public Task<ApiResult<PagedResult<RequestListItem>>> ListRequestsAsync(
        IReadOnlyCollection<string> statuses, bool negativeFeedbackOnly, int page, CancellationToken cancellationToken)
    {
        var url = new StringBuilder("api/admin/chat/requests?page=").Append(page.ToString(CultureInfo.InvariantCulture));
        foreach (var status in statuses)
        {
            url.Append("&status=").Append(Uri.EscapeDataString(status));
        }

        if (negativeFeedbackOnly)
        {
            url.Append("&feedback=negative");
        }

        return GetAsync<PagedResult<RequestListItem>>(url.ToString(), cancellationToken);
    }

    public Task<ApiResult<RequestDetail>> GetRequestAsync(Guid id, CancellationToken cancellationToken) =>
        GetAsync<RequestDetail>($"api/admin/chat/requests/{id}", cancellationToken);

    public Task<ApiResult<ChatSummary>> GetSummaryAsync(string window, CancellationToken cancellationToken) =>
        GetAsync<ChatSummary>($"api/admin/chat/summary?window={Uri.EscapeDataString(window)}", cancellationToken);

    public Task<ApiResult<PagedResult<FeedbackListItem>>> ListFeedbackAsync(int page, CancellationToken cancellationToken) =>
        GetAsync<PagedResult<FeedbackListItem>>($"api/admin/chat/feedback?page={page.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<ApiResult<ChatConfiguration>> GetChatConfigurationAsync(CancellationToken cancellationToken) =>
        GetAsync<ChatConfiguration>("api/admin/chat/configuration", cancellationToken);

    public Task<ApiResult<DocumentsHealth>> GetHealthAsync(CancellationToken cancellationToken) =>
        GetAsync<DocumentsHealth>("api/admin/documents/health", cancellationToken);

    public Task<ApiResult<PagedResult<FailureItem>>> ListFailuresAsync(int page, CancellationToken cancellationToken) =>
        GetAsync<PagedResult<FailureItem>>($"api/admin/documents/failures?page={page.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<ApiResult<PagedResult<PrivateDocumentItem>>> ListPrivateDocumentsAsync(int page, CancellationToken cancellationToken) =>
        GetAsync<PagedResult<PrivateDocumentItem>>($"api/admin/documents/private?page={page.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<ApiResult<DocumentsConfiguration>> GetDocumentsConfigurationAsync(CancellationToken cancellationToken) =>
        GetAsync<DocumentsConfiguration>("api/admin/documents/configuration", cancellationToken);

    private Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken cancellationToken) =>
        DocumentsApi.SendAsync<T>(() => httpClient().GetAsync(url, cancellationToken), cancellationToken);
}
