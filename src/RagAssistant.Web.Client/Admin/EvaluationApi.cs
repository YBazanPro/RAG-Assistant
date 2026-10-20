using System.Globalization;
using System.Text.Json;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Admin;

// The Evaluation module's JSON (spec 020). Metrics stay JsonElement: the harness owns their shape, and the tab shows whatever it wrote.

public sealed record EvaluationHeadline(
    int? Cases,
    int? Errors,
    double? RecallAt5,
    double? Mrr,
    double? BehaviourCorrectness,
    double? CitationPrecision,
    double? AnswerLanguageCorrectness,
    double? Groundedness);

public sealed record EvaluationRunSummary(
    string Id,
    DateTimeOffset StartedAt,
    string? ChatModel,
    string? EmbeddingModel,
    string? JudgeModel,
    string? RetrievalMode,
    bool Rerank,
    string DatasetHash,
    string CorpusHash,
    string? GitCommit,
    bool Valid,
    DateTimeOffset UploadedAt,
    EvaluationHeadline Headline);

public sealed record EvaluationRunDetail(EvaluationRunSummary Run, JsonElement Configuration, JsonElement Metrics, JsonElement? Judge, JsonElement Cases);

public sealed record ConfigurationDifference(string Key, JsonElement? A, JsonElement? B);

public sealed record MetricDelta(string Name, double? A, double? B, double? Delta);

public sealed record MetricGroup(string Name, IReadOnlyList<MetricDelta> Metrics);

public sealed record EvaluationComparison(
    EvaluationRunSummary A,
    EvaluationRunSummary B,
    bool DatasetDiffers,
    bool CorpusDiffers,
    IReadOnlyList<ConfigurationDifference> ConfigurationDifferences,
    IReadOnlyList<MetricGroup> Groups);

/// <summary>The maintainers' evaluation endpoints (spec 020). The harness uploads; the admin tab reads, compares and deletes.</summary>
public interface IEvaluationApi
{
    Task<ApiResult<PagedResult<EvaluationRunSummary>>> ListRunsAsync(int page, CancellationToken cancellationToken);

    Task<ApiResult<EvaluationRunDetail>> GetRunAsync(string id, CancellationToken cancellationToken);

    Task<ApiResult<EvaluationComparison>> CompareAsync(string a, string b, CancellationToken cancellationToken);

    Task<ApiResult<bool>> DeleteRunAsync(string id, CancellationToken cancellationToken);
}

public sealed class EvaluationApi(Func<HttpClient> httpClient) : IEvaluationApi
{
    public Task<ApiResult<PagedResult<EvaluationRunSummary>>> ListRunsAsync(int page, CancellationToken cancellationToken) =>
        Get<PagedResult<EvaluationRunSummary>>($"api/admin/evaluation/runs?page={page.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<ApiResult<EvaluationRunDetail>> GetRunAsync(string id, CancellationToken cancellationToken) =>
        Get<EvaluationRunDetail>($"api/admin/evaluation/runs/{Uri.EscapeDataString(id)}", cancellationToken);

    public Task<ApiResult<EvaluationComparison>> CompareAsync(string a, string b, CancellationToken cancellationToken) =>
        Get<EvaluationComparison>($"api/admin/evaluation/compare?a={Uri.EscapeDataString(a)}&b={Uri.EscapeDataString(b)}", cancellationToken);

    public async Task<ApiResult<bool>> DeleteRunAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient().DeleteAsync($"api/admin/evaluation/runs/{Uri.EscapeDataString(id)}", cancellationToken);
            return response.IsSuccessStatusCode
                ? ApiResult.Ok(true)
                : ApiResult.Fail<bool>(await DocumentsApi.ToErrorAsync(response, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<bool>(new ApiError(0, "network", "The server could not be reached.", DocumentsApi.NoFields));
        }
    }

    private Task<ApiResult<T>> Get<T>(string url, CancellationToken cancellationToken) =>
        DocumentsApi.SendAsync<T>(() => httpClient().GetAsync(url, cancellationToken), cancellationToken);
}
