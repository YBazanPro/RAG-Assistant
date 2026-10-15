namespace RagAssistant.Web.Client.Documents;

// The client owns its copy of the Host's JSON (it cannot reference a module); members the client does not read are simply absent.

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
    DateTimeOffset CreatedAt);

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
    DateTimeOffset CreatedAt);

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
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

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
    string CreatedBy,
    DateTimeOffset? IndexedAt,
    RunInfo[] Runs);

public sealed record VersionList(VersionItem[] Items);

public sealed record PassageItem(
    Guid Id,
    int Position,
    string[] SectionPath,
    int SectionOrdinal,
    string Text,
    int CharCount,
    int TokenEstimate);

public sealed record PassagePage(
    int VersionNumber,
    string VersionStatus,
    bool IsActive,
    IReadOnlyList<PassageItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record ImportAccepted(Guid DocumentId, Guid VersionId, int VersionNumber, Guid IngestionRunId, string Status);

public sealed record RetryAccepted(Guid DocumentId, Guid VersionId, Guid IngestionRunId, int Attempt, string Status);

public sealed record IngestionStageEvent(
    Guid RunId,
    Guid VersionId,
    int VersionNumber,
    int Attempt,
    string Stage,
    string Status,
    string? Message,
    DateTimeOffset OccurredAt)
{
    /// <summary>The run is over: it failed, or the last stage completed.</summary>
    public bool IsTerminal => Status == "failed" || (Stage == "completed" && Status == "completed");
}

public sealed record ImportRequest(
    string FileName,
    Stream Content,
    string Title,
    string OwnerName,
    string Language,
    string[] Tags,
    string? EffectiveDate,
    string? Source);

/// <summary>A failed API call. <see cref="Status"/> is 0 when the Host could not be reached at all.</summary>
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
