using RagAssistant.Web.Client.Admin;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Admin;

/// <summary>A scripted <see cref="IAdminApi"/>: every call succeeds with sample data unless a test replaces its handler.</summary>
public sealed class FakeAdminApi : IAdminApi
{
    public static readonly Guid RequestId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    public Func<ApiResult<DocumentsHealth>> OnHealth { get; set; } = () => ApiResult.Ok(Samples.Health());

    public Func<string, ApiResult<ChatSummary>> OnSummary { get; set; } = window => ApiResult.Ok(Samples.Summary(window));

    public Func<IReadOnlyCollection<string>, bool, int, ApiResult<PagedResult<RequestListItem>>> OnRequests { get; set; } =
        (_, _, _) => ApiResult.Ok(new PagedResult<RequestListItem>([Samples.Request()], 1, 25, 1));

    public Func<Guid, ApiResult<RequestDetail>> OnRequest { get; set; } = id => ApiResult.Ok(Samples.Detail(id));

    public Func<int, ApiResult<PagedResult<FailureItem>>> OnFailures { get; set; } =
        _ => ApiResult.Ok(new PagedResult<FailureItem>([Samples.Failure(), Samples.Failure("Secret plan", "private", canRetry: false)], 1, 25, 2));

    public Func<int, ApiResult<PagedResult<FeedbackListItem>>> OnFeedback { get; set; } =
        _ => ApiResult.Ok(new PagedResult<FeedbackListItem>([new("report", "incorrect", "Wrong threshold", DateTimeOffset.UnixEpoch, RequestId, "Who approves?", "owner-sub")], 1, 25, 1));

    public Func<int, ApiResult<PagedResult<PrivateDocumentItem>>> OnPrivate { get; set; } =
        _ => ApiResult.Ok(new PagedResult<PrivateDocumentItem>([new(Guid.NewGuid(), "Secret plan", "owner-sub", "active", "indexed", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)], 1, 25, 1));

    public List<(IReadOnlyCollection<string> Statuses, bool Negative, int Page)> RequestCalls { get; } = [];

    public List<string> SummaryCalls { get; } = [];

    public List<Guid> DetailCalls { get; } = [];

    public Task<ApiResult<PagedResult<RequestListItem>>> ListRequestsAsync(
        IReadOnlyCollection<string> statuses, bool negativeFeedbackOnly, int page, CancellationToken cancellationToken)
    {
        RequestCalls.Add((statuses, negativeFeedbackOnly, page));
        return Task.FromResult(OnRequests(statuses, negativeFeedbackOnly, page));
    }

    public Task<ApiResult<RequestDetail>> GetRequestAsync(Guid id, CancellationToken cancellationToken)
    {
        DetailCalls.Add(id);
        return Task.FromResult(OnRequest(id));
    }

    public Task<ApiResult<ChatSummary>> GetSummaryAsync(string window, CancellationToken cancellationToken)
    {
        SummaryCalls.Add(window);
        return Task.FromResult(OnSummary(window));
    }

    public Task<ApiResult<PagedResult<FeedbackListItem>>> ListFeedbackAsync(int page, CancellationToken cancellationToken) =>
        Task.FromResult(OnFeedback(page));

    public Task<ApiResult<ChatConfiguration>> GetChatConfigurationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ApiResult.Ok(new ChatConfiguration("Demo", "demo-chat", "014.1", "rw.014.1", 8, 3, 1000, 1000, 200, 10, 1000, 8192, 30, 120, 10, true)));

    public Task<ApiResult<DocumentsHealth>> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(OnHealth());

    public Task<ApiResult<PagedResult<FailureItem>>> ListFailuresAsync(int page, CancellationToken cancellationToken) =>
        Task.FromResult(OnFailures(page));

    public Task<ApiResult<PagedResult<PrivateDocumentItem>>> ListPrivateDocumentsAsync(int page, CancellationToken cancellationToken) =>
        Task.FromResult(OnPrivate(page));

    public Task<ApiResult<DocumentsConfiguration>> GetDocumentsConfigurationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ApiResult.Ok(new DocumentsConfiguration("bge-m3", 1024, 400, 50, 80, 1, 16, 60, 3, 8, 20, 0.5, 0.6, 0.8, 1_048_576, 104_857_600, 2000, "file system", 180)));

    public static class Samples
    {
        public static DocumentsHealth Health() =>
            new(new DocumentCounts(4, 1, 1, 0), new VersionCounts(1, 0, 5, 1, 2), 1, 125, 2, 180);

        public static ChatSummary Summary(string window, int requests = 3) => new(
            window,
            requests,
            new Dictionary<string, int> { ["complete"] = 2, ["refused"] = 1 },
            requests == 0 ? 0 : 1d / 3,
            new Dictionary<string, int> { ["Sufficient"] = 2, ["NoEvidence"] = 1 },
            new Percentiles(400, 900),
            new Percentiles(1500, 2500),
            [new ErrorCount("rewrite", "rewrite.timeout", 1)],
            1);

        public static RequestListItem Request(string question = "Who approves a budget exception?", string status = "complete") =>
            new(RequestId, DateTimeOffset.UnixEpoch, "0123456789abcdef", question, status, null, null, "Sufficient", 0.91, 1200, 1, false);

        public static FailureItem Failure(string title = "Budget Policy", string visibility = "shared", bool canRetry = true) =>
            new(Guid.NewGuid(), Guid.NewGuid(), 2, title, visibility, "owner-sub", "embedding", "embedding.failed", "The embedding model did not answer.", DateTimeOffset.UnixEpoch, 1, canRetry);

        public static RequestDetail Detail(Guid id, string? titleOfPassage = "Budget Policy") => new(
            id,
            Guid.NewGuid(),
            1,
            "0123456789abcdef",
            DateTimeOffset.UnixEpoch,
            new RequestQuestion("And above that amount?", "en", ["finance"], [new ScopedDocument(Guid.NewGuid(), null)], []),
            new RequestRewrite(true, "Who approves a budget exception above 10 000?", null, 120, 80, 12, "rw.014.1"),
            new RequestRetrieval(
                "Sufficient", 0.5, 0.7, 0.8, 0.91, "bge-m3", 14,
                [
                    new RequestPassage(1, 1, 0.91, true, Guid.NewGuid(), Guid.NewGuid(), titleOfPassage, 3, ["Approval"], 4, 4),
                    new RequestPassage(2, 2, 0.58, false, Guid.NewGuid(), Guid.NewGuid(), null, 1, [], null, null),
                ]),
            new RequestGeneration("demo-chat", "014.1", 900, 120, false, 0, 300, 1100),
            new RequestResult("complete", null, null),
            new RequestFeedback("not_helpful", [new FeedbackReport("incorrect", "Wrong threshold", DateTimeOffset.UnixEpoch)]),
            "0123456789abcdef0123456789abcdef",
            "https://traces.test/detail/0123456789abcdef0123456789abcdef",
            false);
    }
}
