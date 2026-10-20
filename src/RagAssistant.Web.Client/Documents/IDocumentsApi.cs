namespace RagAssistant.Web.Client.Documents;

/// <summary>The Documents endpoints as the UI uses them. Pages depend on this so component tests can script the API.</summary>
public interface IDocumentsApi
{
    Task<ApiResult<PagedResult<DocumentSummary>>> ListAsync(DocumentQuery query, CancellationToken cancellationToken);

    Task<ApiResult<Facets>> GetFacetsAsync(CancellationToken cancellationToken);

    Task<ApiResult<DocumentInfo>> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ApiResult<VersionList>> GetVersionsAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ApiResult<PassagePage>> GetPassagesAsync(Guid documentId, int versionNumber, int page, Guid? focus, CancellationToken cancellationToken);

    Task<ApiResult<RetryAccepted>> RetryAsync(Guid documentId, CancellationToken cancellationToken);

    /// <param name="revision">The document's current revision; sent as <c>If-Match</c>.</param>
    Task<ApiResult<DocumentInfo>> UpdateAsync(Guid documentId, int revision, MetadataUpdate update, CancellationToken cancellationToken);

    Task<ApiResult<VersionAccepted>> ReprocessAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ApiResult<DocumentInfo>> ArchiveAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ApiResult<DocumentInfo>> RestoreAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Spec 020: suggested questions drawn at random from readable documents.</summary>
    Task<ApiResult<IReadOnlyList<SuggestedQuestion>>> GetSuggestionsAsync(int count, CancellationToken cancellationToken);

    Task<ApiResult<CorpusSummary>> GetSummaryAsync(CancellationToken cancellationToken);

    /// <param name="revision">The document's current revision; sent as <c>If-Match</c>.</param>
    Task<ApiResult<DocumentInfo>> UpdateSuggestionsAsync(Guid documentId, int revision, IReadOnlyList<string> questions, CancellationToken cancellationToken);
}
