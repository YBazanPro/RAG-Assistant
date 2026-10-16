using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace RagAssistant.Web.Client.Documents;

/// <summary>
/// Typed client over the BFF's <see cref="HttpClient"/>. The client is resolved lazily so a page can be prerendered on the
/// server, where no such client exists, as long as it makes no call before it is interactive.
/// </summary>
public sealed class DocumentsApi(Func<HttpClient> httpClient) : IDocumentsApi
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Http => httpClient();

    public Task<ApiResult<PagedResult<DocumentSummary>>> ListAsync(DocumentQuery query, CancellationToken cancellationToken)
    {
        var queryString = query.ToQueryString();
        return GetAsync<PagedResult<DocumentSummary>>(queryString.Length == 0 ? "api/documents" : $"api/documents?{queryString}", cancellationToken);
    }

    public Task<ApiResult<Facets>> GetFacetsAsync(CancellationToken cancellationToken) =>
        GetAsync<Facets>("api/documents/facets", cancellationToken);

    public Task<ApiResult<DocumentInfo>> GetAsync(Guid documentId, CancellationToken cancellationToken) =>
        GetAsync<DocumentInfo>($"api/documents/{documentId}", cancellationToken);

    public Task<ApiResult<VersionList>> GetVersionsAsync(Guid documentId, CancellationToken cancellationToken) =>
        GetAsync<VersionList>($"api/documents/{documentId}/versions", cancellationToken);

    public Task<ApiResult<PassagePage>> GetPassagesAsync(
        Guid documentId, int versionNumber, int page, Guid? focus, CancellationToken cancellationToken)
    {
        var url = $"api/documents/{documentId}/versions/{versionNumber}/passages?page={page.ToString(CultureInfo.InvariantCulture)}";
        if (focus is { } id)
        {
            url += $"&focus={id}";
        }

        return GetAsync<PassagePage>(url, cancellationToken);
    }

    public Task<ApiResult<RetryAccepted>> RetryAsync(Guid documentId, CancellationToken cancellationToken) =>
        SendAsync<RetryAccepted>(() => Http.PostAsync($"api/documents/{documentId}/ingestion/retry", content: null, cancellationToken), cancellationToken);

    public Task<ApiResult<DocumentInfo>> UpdateAsync(Guid documentId, int revision, MetadataUpdate update, CancellationToken cancellationToken) =>
        SendAsync<DocumentInfo>(() =>
        {
            // The request is disposed by the client once sent; the body is small JSON.
            var request = new HttpRequestMessage(HttpMethod.Put, $"api/documents/{documentId}") { Content = JsonContent.Create(update, options: Json) };
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"");
            return Http.SendAsync(request, cancellationToken);
        }, cancellationToken);

    public Task<ApiResult<VersionAccepted>> ReprocessAsync(Guid documentId, CancellationToken cancellationToken) =>
        SendAsync<VersionAccepted>(() => Http.PostAsync($"api/documents/{documentId}/reprocess", content: null, cancellationToken), cancellationToken);

    public Task<ApiResult<DocumentInfo>> ArchiveAsync(Guid documentId, CancellationToken cancellationToken) =>
        SendAsync<DocumentInfo>(() => Http.PostAsync($"api/documents/{documentId}/archive", content: null, cancellationToken), cancellationToken);

    public Task<ApiResult<DocumentInfo>> RestoreAsync(Guid documentId, CancellationToken cancellationToken) =>
        SendAsync<DocumentInfo>(() => Http.PostAsync($"api/documents/{documentId}/restore", content: null, cancellationToken), cancellationToken);

    private Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken cancellationToken) =>
        SendAsync<T>(() => Http.GetAsync(url, cancellationToken), cancellationToken);

    internal static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            if (!response.IsSuccessStatusCode)
            {
                return ApiResult.Fail<T>(await ToErrorAsync(response, cancellationToken));
            }

            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
            return value is null
                ? ApiResult.Fail<T>(new ApiError((int)response.StatusCode, "response.empty", "The server sent an empty response.", NoFields))
                : ApiResult.Ok<T>(value);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<T>(new ApiError(0, "network", "The server could not be reached.", NoFields));
        }
        catch (JsonException)
        {
            return ApiResult.Fail<T>(new ApiError(0, "response.malformed", "The server sent an unreadable response.", NoFields));
        }
    }

    internal static readonly IReadOnlyDictionary<string, string[]> NoFields = new Dictionary<string, string[]>();

    /// <summary>Reads the outcome of an upload the browser made itself into the same result type every other call returns.</summary>
    public static ApiResult<T> FromUpload<T>(UploadResult upload)
    {
        if (upload.IsNetworkError || upload.IsAborted || upload.IsMissingFile)
        {
            return ApiResult.Fail<T>(new ApiError(0, "network", "The server could not be reached.", NoFields));
        }

        if (!upload.IsSuccess)
        {
            return ApiResult.Fail<T>(ErrorFromBody(upload.Status, null, upload.Body));
        }

        try
        {
            var value = upload.Body is null ? default : JsonSerializer.Deserialize<T>(upload.Body, Json);
            return value is null
                ? ApiResult.Fail<T>(new ApiError(upload.Status, "response.empty", "The server sent an empty response.", NoFields))
                : ApiResult.Ok(value);
        }
        catch (JsonException)
        {
            return ApiResult.Fail<T>(new ApiError(0, "response.malformed", "The server sent an unreadable response.", NoFields));
        }
    }

    // The API and the BFF both answer errors with ProblemDetails carrying a stable `code`; anything else is reduced to its status.
    internal static async Task<ApiError> ToErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? text = null;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // No readable content.
        }

        return ErrorFromBody((int)response.StatusCode, response.ReasonPhrase, text);
    }

    internal static ApiError ErrorFromBody(int status, string? reasonPhrase, string? text)
    {
        string code = $"http.{status}";
        string message = reasonPhrase ?? "The request failed.";
        IReadOnlyDictionary<string, string[]> fields = NoFields;
        Guid? existing = null;

        try
        {
            var body = string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text, Json);
            if (body.ValueKind == JsonValueKind.Object)
            {
                code = body.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : code;
                message = body.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()!
                    : body.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : message;
                if (body.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    fields = e.EnumerateObject()
                        .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                        .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(m => m.GetString() ?? string.Empty).ToArray(), StringComparer.OrdinalIgnoreCase);
                }

                if (body.TryGetProperty("existingDocumentId", out var x) && x.ValueKind == JsonValueKind.String && x.TryGetGuid(out var id))
                {
                    existing = id;
                }
            }
        }
        catch (JsonException)
        {
            // No ProblemDetails body (for example a proxy's 413); the status alone decides the message.
        }
        catch (NotSupportedException)
        {
            // A non-JSON content type.
        }

        return new ApiError(status, code, message, fields, existing);
    }
}
