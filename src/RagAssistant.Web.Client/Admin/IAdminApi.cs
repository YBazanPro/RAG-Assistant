using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Admin;

/// <summary>The maintainers' admin endpoints as the UI uses them (spec 017). Retrying an ingestion goes through <see cref="IDocumentsApi"/>.</summary>
public interface IAdminApi
{
    Task<ApiResult<PagedResult<RequestListItem>>> ListRequestsAsync(
        IReadOnlyCollection<string> statuses, bool negativeFeedbackOnly, int page, CancellationToken cancellationToken);

    Task<ApiResult<RequestDetail>> GetRequestAsync(Guid id, CancellationToken cancellationToken);

    Task<ApiResult<ChatSummary>> GetSummaryAsync(string window, CancellationToken cancellationToken);

    Task<ApiResult<PagedResult<FeedbackListItem>>> ListFeedbackAsync(int page, CancellationToken cancellationToken);

    Task<ApiResult<ChatConfiguration>> GetChatConfigurationAsync(CancellationToken cancellationToken);

    Task<ApiResult<DocumentsHealth>> GetHealthAsync(CancellationToken cancellationToken);

    Task<ApiResult<PagedResult<FailureItem>>> ListFailuresAsync(int page, CancellationToken cancellationToken);

    Task<ApiResult<PagedResult<PrivateDocumentItem>>> ListPrivateDocumentsAsync(int page, CancellationToken cancellationToken);

    Task<ApiResult<DocumentsConfiguration>> GetDocumentsConfigurationAsync(CancellationToken cancellationToken);
}
