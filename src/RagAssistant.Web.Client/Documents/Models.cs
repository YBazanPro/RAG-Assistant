namespace RagAssistant.Web.Client.Documents;

// The client owns its copy of the API's JSON (it cannot reference a module); members the client does not read are simply absent.

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record DocumentSummary(
    Guid Id,
    string Title,
    string OwnerName,
    string Language,
    string[] Tags,
    DateOnly? EffectiveDate,
    string Status,
    int? ActiveVersionNumber,
    int? LatestVersionNumber,
    int? PassageCount,
    DateTimeOffset LastActivityAt,
    DateTimeOffset CreatedAt,
    string Visibility = "shared",
    bool IsOwner = false);

public sealed record FacetValue(string Value, int Count);

public sealed record Facets(FacetValue[] Owners, FacetValue[] Languages, FacetValue[] Tags);

public sealed record RunInfo(
    Guid Id,
    int Attempt,
    string Status,
    string Stage,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record LatestVersionInfo(
    Guid Id,
    int Number,
    string Status,
    string SourceFormat,
    string OriginalFileName,
    int SizeBytes,
    int? PassageCount,
    DateTimeOffset CreatedAt,
    int[]? PagesWithoutText = null);

public sealed record DocumentInfo(
    Guid Id,
    string Title,
    string OwnerName,
    string Language,
    string[] Tags,
    DateOnly? EffectiveDate,
    string? Source,
    string Status,
    Guid? ActiveVersionId,
    LatestVersionInfo? LatestVersion,
    RunInfo? LatestRun,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy,
    int Revision = 0,
    string Visibility = "shared",
    bool IsOwner = false,
    string LanguageSource = "owner",
    string? DetectedLanguage = null,
    string? LanguageDetection = null,
    IReadOnlyList<string>? SuggestedQuestions = null,
    string? SuggestionsStatus = null,
    string? SuggestionsError = null);

/// <summary>A suggested question of a readable document (spec 020).</summary>
public sealed record SuggestedQuestion(string Question, Guid DocumentId, string DocumentTitle, string Language);

public sealed record LanguageCount(string Language, int Count);

public sealed record RecentDocument(Guid Id, string Title, DateTimeOffset UpdatedAt, string Language);

/// <summary>What the home page says about the corpus the user can read (spec 020).</summary>
public sealed record CorpusSummary(int ActiveDocuments, IReadOnlyList<LanguageCount> ByLanguage, DateTimeOffset? LastIngestedAt, IReadOnlyList<RecentDocument> Recent);

public sealed record VersionItem(
    Guid Id,
    int Number,
    string Status,
    string Origin,
    string SourceFormat,
    string OriginalFileName,
    int SizeBytes,
    int? PassageCount,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset? IndexedAt,
    RunInfo[] Runs,
    int[]? PagesWithoutText = null);

public sealed record VersionList(VersionItem[] Items);

public sealed record PassageItem(
    Guid Id,
    int Position,
    string[] SectionPath,
    int SectionOrdinal,
    string Text,
    int CharCount,
    int TokenEstimate,
    int? PageStart = null,
    int? PageEnd = null);

public sealed record PassagePage(
    int VersionNumber,
    string VersionStatus,
    bool IsActive,
    IReadOnlyList<PassageItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record ImportAccepted(Guid DocumentId, Guid VersionId, int VersionNumber, Guid IngestionRunId, string Status);

/// <summary>Answer of "upload a new version" and "reprocess" (spec 009).</summary>
public sealed record VersionAccepted(Guid DocumentId, Guid VersionId, int VersionNumber, Guid IngestionRunId, string Status);

public sealed record RetryAccepted(Guid DocumentId, Guid VersionId, Guid IngestionRunId, int Attempt, string Status);

public sealed record IngestionStageEvent(
    Guid RunId,
    Guid VersionId,
    int VersionNumber,
    int Attempt,
    string Stage,
    string Status,
    string? Message,
    DateTimeOffset OccurredAt,
    int? Current = null,
    int? Total = null)
{
    /// <summary>A stage reporting how far it has got (pages read, passages embedded).</summary>
    public bool IsProgress => Status == "progress" && Total is > 0;

    /// <summary>The run is over: it failed, or the last stage completed.</summary>
    public bool IsTerminal => Status == "failed" || (Stage == "completed" && Status == "completed");
}

/// <param name="EffectiveDate">Raw <c>yyyy-MM-dd</c> text or null.</param>
public sealed record MetadataUpdate(string Title, string OwnerName, string Language, string[] Tags, string? EffectiveDate, string? Source, string? Visibility = null);

/// <summary>A failed API call. <see cref="Status"/> is 0 when the API could not be reached at all.</summary>
public sealed record ApiError(
    int Status,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]> Fields,
    Guid? ExistingDocumentId = null)
{
    public bool IsUnauthenticated => Status == 401;

    public bool IsNotFound => Status == 404;
}

public static class ApiResult
{
    public static ApiResult<T> Ok<T>(T value) => new(value, null);

    public static ApiResult<T> Fail<T>(ApiError error) => new(default, error);
}

public sealed class ApiResult<T>
{
    private readonly T? _value;

    internal ApiResult(T? value, ApiError? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public ApiError? Error { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("The call failed.");
}
