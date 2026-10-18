using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RagAssistant.Evaluation.Api;

/// <summary>
/// The HTTP API as the harness uses it (spec 018): only public endpoints, never the database or an internal type. Calls go straight
/// to the API with a bearer token, as the dev tokens allow; the BFF is not involved.
/// </summary>
internal sealed partial class RagAssistantApi(HttpClient http, DevTokens tokens)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DocumentSummary>> FindByTagAsync(string user, string tag, CancellationToken cancellationToken)
    {
        // The default listing leaves archived documents out; the harness needs those too, to restore them.
        var found = new List<DocumentSummary>();
        foreach (var status in new[] { "", "archived" })
        {
            for (var page = 1; ; page++)
            {
                var url = $"api/documents?tags={Uri.EscapeDataString(tag)}&pageSize=100&page={page}" + (status.Length > 0 ? $"&status={status}" : "");
                var result = await SendAsync<DocumentPage>(user, new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
                var body = result.Require("list documents");
                found.AddRange(body.Items);
                if (page * body.PageSize >= body.TotalCount)
                {
                    break;
                }
            }
        }

        return [.. found.DistinctBy(d => d.Id)];
    }

    public async Task<ApiResponse<DocumentDetail>> GetAsync(string user, Guid documentId, CancellationToken cancellationToken) =>
        await SendAsync<DocumentDetail>(user, new HttpRequestMessage(HttpMethod.Get, $"api/documents/{documentId}"), cancellationToken);

    public async Task<ApiResponse<Accepted>> ImportAsync(
        string user, SourceFile file, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        var form = Form(file, fields);
        return await SendAsync<Accepted>(user, new HttpRequestMessage(HttpMethod.Post, "api/documents") { Content = form }, cancellationToken);
    }

    public async Task<ApiResponse<Accepted>> UploadVersionAsync(string user, Guid documentId, SourceFile file, CancellationToken cancellationToken) =>
        await SendAsync<Accepted>(
            user, new HttpRequestMessage(HttpMethod.Post, $"api/documents/{documentId}/versions") { Content = Form(file, []) }, cancellationToken);

    public async Task<ApiResponse<DocumentDetail>> RestoreAsync(string user, Guid documentId, CancellationToken cancellationToken) =>
        await SendAsync<DocumentDetail>(user, new HttpRequestMessage(HttpMethod.Post, $"api/documents/{documentId}/restore"), cancellationToken);

    public async Task<ApiResponse<DocumentDetail>> UpdateAsync(
        string user, Guid documentId, int revision, MetadataUpdate update, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/documents/{documentId}") { Content = JsonContent.Create(update, options: Json) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return await SendAsync<DocumentDetail>(user, request, cancellationToken);
    }

    private static MultipartFormDataContent Form(SourceFile file, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        var content = new ByteArrayContent(file.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        form.Add(content, "file", file.FileName);
        return form;
    }

    private async Task<ApiResponse<T>> SendAsync<T>(string user, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(user, cancellationToken));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new HarnessException($"The API at {http.BaseAddress} is not reachable ({ex.Message}). Start the AppHost first.");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
                    return new ApiResponse<T>(response.StatusCode, value, null, null);
                }

                var problem = response.Content.Headers.ContentLength == 0
                    ? default
                    : await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
                var code = problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var c) ? c.GetString() : null;
                return new ApiResponse<T>(response.StatusCode, default, code, problem.ValueKind == JsonValueKind.Object ? problem : null);
            }
        }
    }
}

internal sealed record ApiResponse<T>(HttpStatusCode Status, T? Value, string? Code, JsonElement? Problem)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public T Require(string what) => IsSuccess && Value is not null
        ? Value
        : throw new HarnessException($"Could not {what}: {(int)Status} {Code ?? Problem?.ToString() ?? ""}".TrimEnd());
}

/// <summary>A file to send: its name decides the format on the server (.md, .txt, .pdf).</summary>
internal sealed record SourceFile(string FileName, string ContentType, byte[] Bytes);

internal sealed record DocumentPage(IReadOnlyList<DocumentSummary> Items, int Page, int PageSize, int TotalCount);

internal sealed record DocumentSummary(Guid Id, string Title, string Status);

internal sealed record RunInfo(Guid Id, int Attempt, string Status, string Stage, string? ErrorCode, string? ErrorMessage);

internal sealed record LatestVersionInfo(Guid Id, int Number, string Status);

internal sealed record DocumentDetail(
    Guid Id,
    string Title,
    string OwnerName,
    string Language,
    string[] Tags,
    DateOnly? EffectiveDate,
    string? Source,
    string Status,
    LatestVersionInfo? LatestVersion,
    RunInfo? LatestRun,
    int Revision,
    string Visibility,
    string LanguageSource);

internal sealed record Accepted(Guid DocumentId, Guid VersionId, Guid IngestionRunId);

internal sealed record MetadataUpdate(
    string Title, string OwnerName, string Language, string[] Tags, string? EffectiveDate, string? Source, string? Visibility);
