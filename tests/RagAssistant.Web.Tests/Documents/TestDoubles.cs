using System.Runtime.CompilerServices;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Documents;

/// <summary>Time that only moves when a test says so; supports the timers and <c>Task.Delay</c> the UI uses.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.IsScheduled);
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }

        while (true)
        {
            ManualTimer? due;
            lock (_gate)
            {
                due = _timers.Where(t => t.IsScheduled && t.Due <= _now).OrderBy(t => t.Due).FirstOrDefault();
            }

            if (due is null)
            {
                return;
            }

            due.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset Due { get; private set; }

        public bool IsScheduled { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                IsScheduled = dueTime != Timeout.InfiniteTimeSpan;
                Due = owner._now + (IsScheduled ? dueTime : TimeSpan.Zero);
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                {
                    IsScheduled = false;
                }
                else
                {
                    Due = owner._now + _period;
                }
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                IsScheduled = false;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A scriptable API for component tests; every call is recorded.</summary>
public sealed class FakeDocumentsApi : IDocumentsApi
{
    public Func<DocumentQuery, ApiResult<PagedResult<DocumentSummary>>> OnList { get; set; } =
        _ => ApiResult.Ok(Samples.Page());

    public Func<ApiResult<Facets>> OnFacets { get; set; } = () => ApiResult.Ok(Samples.SampleFacets());

    public Func<Guid, ApiResult<DocumentInfo>> OnGet { get; set; } = id => ApiResult.Ok(Samples.Detail(id));

    public Func<Guid, ApiResult<VersionList>> OnVersions { get; set; } = _ => ApiResult.Ok(new VersionList([Samples.Version(1, isActive: true)]));

    public Func<int, int, Guid?, ApiResult<PassagePage>> OnPassages { get; set; } =
        (version, page, _) => ApiResult.Ok(Samples.Passages(version, page, 3));

    public Func<Guid, ApiResult<RetryAccepted>> OnRetry { get; set; } =
        id => ApiResult.Ok(new RetryAccepted(id, Guid.NewGuid(), Guid.NewGuid(), 2, "queued"));

    /// <summary>Imports and new versions no longer go through the API client but through the browser uploader (spec 010).</summary>
    public FakeFileUploader Uploader { get; } = new();

    public Func<Guid, int, MetadataUpdate, ApiResult<DocumentInfo>> OnUpdate { get; set; } =
        (id, revision, update) => ApiResult.Ok(Samples.Detail(id, revision: revision + 1) with { Title = update.Title });

    public Func<Guid, ApiResult<VersionAccepted>> OnReprocess { get; set; } =
        id => ApiResult.Ok(new VersionAccepted(id, Guid.NewGuid(), 2, Guid.NewGuid(), "queued"));

    public Func<Guid, ApiResult<DocumentInfo>> OnArchive { get; set; } = id => ApiResult.Ok(Samples.Detail(id, "archived"));

    public Func<Guid, ApiResult<DocumentInfo>> OnRestore { get; set; } = id => ApiResult.Ok(Samples.Detail(id));

    public List<(Guid Id, int Revision, MetadataUpdate Update)> UpdateCalls { get; } = [];

    public List<Guid> ReprocessCalls { get; } = [];

    public List<Guid> ArchiveCalls { get; } = [];

    public List<Guid> RestoreCalls { get; } = [];

    public List<DocumentQuery> ListCalls { get; } = [];

    public List<(Guid Id, int Version, int Page, Guid? Focus)> PassageCalls { get; } = [];

    public List<Guid> RetryCalls { get; } = [];

    public int FacetsCalls { get; private set; }

    /// <summary>When set, list calls wait for it, so a test can observe the loading state.</summary>
    public Task? ListGate { get; set; }

    public async Task<ApiResult<PagedResult<DocumentSummary>>> ListAsync(DocumentQuery query, CancellationToken cancellationToken)
    {
        ListCalls.Add(query);
        if (ListGate is not null)
        {
            await ListGate;
        }

        return OnList(query);
    }

    public Task<ApiResult<Facets>> GetFacetsAsync(CancellationToken cancellationToken)
    {
        FacetsCalls++;
        return Task.FromResult(OnFacets());
    }

    public Task<ApiResult<DocumentInfo>> GetAsync(Guid documentId, CancellationToken cancellationToken) =>
        Task.FromResult(OnGet(documentId));

    public Task<ApiResult<VersionList>> GetVersionsAsync(Guid documentId, CancellationToken cancellationToken) =>
        Task.FromResult(OnVersions(documentId));

    public Task<ApiResult<PassagePage>> GetPassagesAsync(Guid documentId, int versionNumber, int page, Guid? focus, CancellationToken cancellationToken)
    {
        PassageCalls.Add((documentId, versionNumber, page, focus));
        return Task.FromResult(OnPassages(versionNumber, page, focus));
    }

    public Task<ApiResult<RetryAccepted>> RetryAsync(Guid documentId, CancellationToken cancellationToken)
    {
        RetryCalls.Add(documentId);
        return Task.FromResult(OnRetry(documentId));
    }

    public Task<ApiResult<DocumentInfo>> UpdateAsync(Guid documentId, int revision, MetadataUpdate update, CancellationToken cancellationToken)
    {
        UpdateCalls.Add((documentId, revision, update));
        return Task.FromResult(OnUpdate(documentId, revision, update));
    }

    public Task<ApiResult<VersionAccepted>> ReprocessAsync(Guid documentId, CancellationToken cancellationToken)
    {
        ReprocessCalls.Add(documentId);
        return Task.FromResult(OnReprocess(documentId));
    }

    public Task<ApiResult<DocumentInfo>> ArchiveAsync(Guid documentId, CancellationToken cancellationToken)
    {
        ArchiveCalls.Add(documentId);
        return Task.FromResult(OnArchive(documentId));
    }

    public Task<ApiResult<DocumentInfo>> RestoreAsync(Guid documentId, CancellationToken cancellationToken)
    {
        RestoreCalls.Add(documentId);
        return Task.FromResult(OnRestore(documentId));
    }

    /// <summary>Spec 020: none by default, so pages fall back as they do on an empty corpus.</summary>
    public Func<int, ApiResult<IReadOnlyList<SuggestedQuestion>>> OnSuggestions { get; set; } = _ => ApiResult.Ok<IReadOnlyList<SuggestedQuestion>>([]);

    public Func<ApiResult<CorpusSummary>> OnSummary { get; set; } = () => ApiResult.Ok(new CorpusSummary(0, [], null, []));

    public Func<Guid, int, IReadOnlyList<string>, ApiResult<DocumentInfo>> OnUpdateSuggestions { get; set; } =
        (id, _, questions) => ApiResult.Ok(Samples.Detail(id) with { SuggestedQuestions = questions, SuggestionsStatus = "edited" });

    public List<(Guid Id, int Revision, IReadOnlyList<string> Questions)> SuggestionUpdateCalls { get; } = [];

    public Task<ApiResult<IReadOnlyList<SuggestedQuestion>>> GetSuggestionsAsync(int count, CancellationToken cancellationToken) =>
        Task.FromResult(OnSuggestions(count));

    public Task<ApiResult<CorpusSummary>> GetSummaryAsync(CancellationToken cancellationToken) => Task.FromResult(OnSummary());

    public Task<ApiResult<DocumentInfo>> UpdateSuggestionsAsync(
        Guid documentId, int revision, IReadOnlyList<string> questions, CancellationToken cancellationToken)
    {
        SuggestionUpdateCalls.Add((documentId, revision, questions));
        return Task.FromResult(OnUpdateSuggestions(documentId, revision, questions));
    }

}

/// <summary>Stands in for the browser's file upload: records the request, can report progress, hold the upload open and be aborted.</summary>
public sealed class FakeFileUploader : IFileUploader
{
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    public List<UploadRequest> Requests { get; } = [];

    /// <summary>How many uploads were aborted through their cancellation token.</summary>
    public int Aborted { get; private set; }

    /// <summary>When set, the upload waits for it (after reporting <see cref="Steps"/>), so a test can observe the uploading state.</summary>
    public Task? Gate { get; set; }

    /// <summary>Progress reported before the gate: the file is "sent" in these steps.</summary>
    public List<UploadProgress> Steps { get; } = [];

    public Func<UploadRequest, UploadResult> OnUpload { get; set; } = request =>
        request.Url == "api/documents"
            ? Accepted(new ImportAccepted(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "queued"))
            : Accepted(new VersionAccepted(Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid(), "queued"));

    public static UploadResult Accepted<T>(T body) => new(202, System.Text.Json.JsonSerializer.Serialize(body, Web));

    /// <summary>The answer the API gives for this error: a ProblemDetails body with the stable code, or a bare status.</summary>
    public static UploadResult Failure(ApiError error)
    {
        if (error.Status == 0)
        {
            return new UploadResult(0, null);
        }

        var body = new Dictionary<string, object?> { ["status"] = error.Status, ["code"] = error.Code, ["detail"] = error.Message };
        if (error.Fields.Count > 0)
        {
            body["errors"] = error.Fields;
        }

        if (error.ExistingDocumentId is { } existing)
        {
            body["existingDocumentId"] = existing;
        }

        return new UploadResult(error.Status, System.Text.Json.JsonSerializer.Serialize(body, Web));
    }

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<UploadProgress> progress, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        foreach (var step in Steps)
        {
            progress.Report(step);
        }

        if (Gate is not null)
        {
            try
            {
                await Gate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Aborted++;
                return new UploadResult(UploadResult.AbortedStatus, null);
            }
        }

        return OnUpload(request);
    }
}

/// <summary>Feeds the ingestion watcher scripted events and failures, one connection at a time.</summary>
public sealed class FakeEventSource : IIngestionEventSource
{
    private readonly Queue<Func<CancellationToken, IAsyncEnumerable<SseEvent>>> _connections = new();

    public List<string?> LastEventIds { get; } = [];

    public int Opened => LastEventIds.Count;

    public int Closed { get; private set; }

    public FakeEventSource EnqueueEvents(params SseEvent[] events)
    {
        _connections.Enqueue(ct => Replay(events, ct));
        return this;
    }

    public FakeEventSource EnqueueFailure(Exception error)
    {
        _connections.Enqueue(_ => Fail(error));
        return this;
    }

    public FakeEventSource EnqueueHang()
    {
        _connections.Enqueue(Hang);
        return this;
    }

    public IAsyncEnumerable<SseEvent> OpenAsync(Guid documentId, string? lastEventId, CancellationToken cancellationToken)
    {
        LastEventIds.Add(lastEventId);
        return Track(_connections.TryDequeue(out var next) ? next(cancellationToken) : Hang(cancellationToken));
    }

    private async IAsyncEnumerable<SseEvent> Track(IAsyncEnumerable<SseEvent> inner)
    {
        try
        {
            await foreach (var item in inner)
            {
                yield return item;
            }
        }
        finally
        {
            Closed++;
        }
    }

    private static async IAsyncEnumerable<SseEvent> Replay(SseEvent[] events, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<SseEvent> Fail(Exception error)
    {
        await Task.Yield();
        if (error is not null)
        {
            throw error;
        }

        yield break;
    }

    private static async IAsyncEnumerable<SseEvent> Hang([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}

public static class Samples
{
    public static readonly Guid DocId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid RunId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset At = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    public static DocumentSummary Summary(string title = "Budget Exception Policy", string status = "active", int? passages = 42, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), title, "Finance", "en", ["budget", "finance"], new DateOnly(2026, 1, 1), status, 1, 1, passages, At, At);

    public static PagedResult<DocumentSummary> Page(params DocumentSummary[] items)
    {
        var rows = items.Length == 0 ? [Summary()] : items;
        return new(rows, 1, 25, rows.Length);
    }

    public static PagedResult<DocumentSummary> Empty() => new([], 1, 25, 0);

    public static Facets SampleFacets() =>
        new([new FacetValue("Finance", 3)], [new FacetValue("en", 3), new FacetValue("fr", 1)], [new FacetValue("budget", 2)]);

    public static RunInfo Run(string status = "succeeded", string stage = "completed", int attempt = 1, string? error = null) =>
        new(RunId, attempt, status, stage, error is null ? null : "ingestion.failed", error, At, At, status is "queued" or "running" ? null : At);

    public static DocumentInfo Detail(Guid id, string status = "active", RunInfo? run = null, int revision = 1, string latestStatus = "indexed") =>
        new(id, "Budget Exception Policy", "Finance", "en", ["budget"], new DateOnly(2026, 1, 1), "https://example.test/policy", status, Guid.NewGuid(),
            new LatestVersionInfo(Guid.NewGuid(), 1, latestStatus, "markdown", "policy.md", 2048, 3, At), run ?? Run(), At, "user-a", At, "user-b", revision);

    public static VersionItem Version(int number, bool isActive = false, string status = "indexed", RunInfo[]? runs = null) =>
        new(Guid.NewGuid(), number, status, "upload", "markdown", "policy.md", 2048, 3, isActive, At, "user-a", At, runs ?? [Run()]);

    public static PassageItem Passage(int position, string text = "Short text", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), position, ["Handbook", "Leave"], 0, text, text.Length, 20);

    public static PassagePage Passages(int version, int page, int count, Guid? firstId = null) =>
        new(version, "indexed", true, [.. Enumerable.Range(0, count).Select(i => Passage(i, id: i == 0 ? firstId : null))], page, 50, count);

    public static SseEvent Stage(string stage, string status, string? id, string? message = null, int attempt = 1)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new IngestionStageEvent(RunId, Guid.NewGuid(), 1, attempt, stage, status, message, At), WebJson);
        return new SseEvent("ingestion.stage", id, json);
    }

    /// <summary>A <c>progress</c> event: <paramref name="current"/> of <paramref name="total"/> pages or passages done in the stage.</summary>
    public static SseEvent Progress(string stage, string? id, int current, int total)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new IngestionStageEvent(RunId, Guid.NewGuid(), 1, 1, stage, "progress", null, At, current, total), WebJson);
        return new SseEvent("ingestion.stage", id, json);
    }

    public static SseEvent Heartbeat() => new("heartbeat", null, "");
}
