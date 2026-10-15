namespace RagAssistant.Web.Client.Documents;

/// <summary>The Documents endpoints as the UI uses them. Pages depend on this so component tests can script the Host.</summary>
public interface IDocumentsApi
{
    Task<ApiResult<PagedResult<DocumentSummary>>> ListAsync(DocumentQuery query, CancellationToken cancellationToken);

    Task<ApiResult<Facets>> GetFacetsAsync(CancellationToken cancellationToken);

    Task<ApiResult<DocumentInfo>> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ApiResult<VersionList>> GetVersionsAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ApiResult<PassagePage>> GetPassagesAsync(Guid documentId, int versionNumber, int page, Guid? focus, CancellationToken cancellationToken);

    Task<ApiResult<RetryAccepted>> RetryAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ApiResult<ImportAccepted>> ImportAsync(ImportRequest request, CancellationToken cancellationToken);
}
