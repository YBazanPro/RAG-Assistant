using System.Text;

namespace RagAssistant.Web.Client.Chat;

public enum LiveOutcome
{
    /// <summary>A terminal event arrived: <c>answer.completed</c> or <c>error</c>.</summary>
    Finished,

    /// <summary>The stream ended or broke without a terminal event; the server state decides what to show.</summary>
    Lost,

    /// <summary>The caller cancelled; the server marks the answer cancelled.</summary>
    Cancelled,
}

/// <summary>
/// The answer being streamed (spec 013): folds events into view state. Deltas go into a buffer and the view is told to render
/// at most every <see cref="RenderInterval"/> (a <see cref="TimeProvider"/> timer) and at once on every other event.
/// </summary>
public sealed class LiveAnswer : IDisposable
{
    public static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private readonly List<string> _announcements = [];
    private ITimer? _timer;
    private IReadOnlyList<Citation> _sources = [];
    private RetrievalCompleted? _retrieval;
    private AnswerCompleted? _completed;
    private AnswerError? _error;
    private bool _stageWriting;
    private bool _lost;

    public LiveAnswer(TimeProvider clock)
    {
        _clock = clock;
        Announce("Searching the documents");
    }

    /// <summary>Raised when the view should render; may come from a timer thread.</summary>
    public event Action? Changed;

    /// <summary>Raised once the server has stored the question (<c>question.accepted</c>).</summary>
    public event Action? Accepted;

    /// <summary>Raised with each stage sentence for the screen-reader region.</summary>
    public event Action<string>? Announced;

    /// <summary>What the user typed; set by the page, so the "Searched for" line knows when the query differs.</summary>
    public string? Question { get; init; }

    /// <summary>What retrieval searches for, once <c>retrieval.started</c> has arrived.</summary>
    public string? RetrievalQuery { get; private set; }

    /// <summary>The query when it differs from the question (trimmed, case-sensitive), otherwise null.</summary>
    public string? SearchedFor => RetrievalQuery is { } query && Question is { } question && query.Trim() != question.Trim() ? query : null;

    public Guid? UserMessageId { get; private set; }

    public Guid? AssistantMessageId { get; private set; }

    public IReadOnlyList<string> Announcements
    {
        get
        {
            lock (_gate)
            {
                return [.. _announcements];
            }
        }
    }

    public async Task<LiveOutcome> ConsumeAsync(IAsyncEnumerable<AnswerEvent> events, CancellationToken cancellationToken)
    {
        var outcome = LiveOutcome.Lost;
        try
        {
            await foreach (var item in events.WithCancellation(cancellationToken))
            {
                if (Apply(item))
                {
                    outcome = LiveOutcome.Finished;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = LiveOutcome.Cancelled;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // A dropped connection is a normal end of the stream here: the server state is fetched next.
            outcome = LiveOutcome.Lost;
        }

        // A terminal event has already rendered; the other endings render once here.
        switch (outcome)
        {
            case LiveOutcome.Lost:
                _lost = true;
                Announce("Connection lost");
                RenderNow();
                break;
            case LiveOutcome.Cancelled:
                Announce("Answer cancelled");
                RenderNow();
                break;
        }

        return outcome;
    }

    /// <summary>The steps known so far (spec 015): the answer step is null until the stored activity replaces this.</summary>
    public ActivityView ToActivity()
    {
        lock (_gate)
        {
            var scope = _retrieval?.Scope;
            var leftOut = _retrieval?.LeftOut ?? [];
            var documents = (scope?.DocumentIds ?? []).Select(id => new ScopeDocument(id, null, !leftOut.Contains(id))).ToList();
            var passages = (_retrieval?.Sources ?? [])
                .Select(s => new PassageStep(
                    s.Marker, s.Marker, s.Score, false, s.DocumentId, s.DocumentTitle, s.VersionNumber, s.SectionPath, s.PageStart, s.PageEnd,
                    s.AdmittedBy))
                .ToList();
            return new ActivityView(
                Question ?? "",
                new RewriteStep(SearchedFor is not null, RetrievalQuery, null, null),
                new ScopeStep(scope?.Language, scope?.Tags ?? [], documents),
                new RetrievalStep(
                    _retrieval?.Outcome, _retrieval?.MinScore, _retrieval?.ConfidentScore, _retrieval?.RetrievalMs, passages, _retrieval?.BestScore,
                    _retrieval?.Mode, _retrieval?.Rerank),
                null);
        }
    }

    public AnswerView ToView()
    {
        lock (_gate)
        {
            var text = _text.ToString();
            if (_completed is { } done)
            {
                var cited = done.Citations.OrderBy(c => c.Marker).ToArray();
                return new AnswerView(done.Status, text, done.RefusalReason, null, false, null, cited, cited, null, AssistantMessageId);
            }

            if (_error is { } error)
            {
                return new AnswerView("failed", text, null, error.Message, error.Retryable, null, _sources, [], null, AssistantMessageId);
            }

            return new AnswerView(
                _lost ? "lost" : "streaming", text, null, null, false, StageLine(), _sources, [], null, AssistantMessageId);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private string StageLine() => _stageWriting
        ? $"Found {_sources.Count} {(_sources.Count == 1 ? "passage" : "passages")}. Writing the answer…"
        : "Searching the documents…";

    // True when the event ends the answer.
    private bool Apply(AnswerEvent item)
    {
        switch (item)
        {
            case QuestionAccepted accepted:
                UserMessageId = accepted.UserMessageId;
                AssistantMessageId = accepted.AssistantMessageId;
                Accepted?.Invoke();
                RenderNow();
                return false;
            case RetrievalStarted started:
                RetrievalQuery = started.Query;
                RenderNow();
                return false;
            case RetrievalCompleted retrieval:
                lock (_gate)
                {
                    _retrieval = retrieval;
                    _sources = [.. retrieval.Sources.Select(Citation.FromSource)];
                    _stageWriting = true;
                }

                Announce("Writing the answer");
                RenderNow();
                return false;
            case AnswerDelta delta:
                lock (_gate)
                {
                    _text.Append(delta.Text);
                    _timer ??= _clock.CreateTimer(OnTimer, null, RenderInterval, Timeout.InfiniteTimeSpan);
                }

                return false;
            case AnswerCompleted completed:
                lock (_gate)
                {
                    _completed = completed;
                }

                Announce(CompletionSentence(completed));
                RenderNow();
                return true;
            case AnswerError error:
                lock (_gate)
                {
                    _error = error;
                }

                Announce($"Error: {error.Message}");
                RenderNow();
                return true;
            default:
                return false;
        }
    }

    private static string CompletionSentence(AnswerCompleted completed)
    {
        var count = completed.Citations.Count;
        var sources = $"{count} {(count == 1 ? "source" : "sources")}";
        return completed.Status switch
        {
            "complete" => $"Answer complete, {sources}",
            "partial" => $"Partial answer, {sources}",
            "refused" => "Could not answer",
            "cancelled" => "Answer cancelled",
            _ => "Could not answer",
        };
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        Changed?.Invoke();
    }

    // Every non-delta event renders at once; the pending delta timer is then redundant.
    private void RenderNow()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        Changed?.Invoke();
    }

    private void Announce(string sentence)
    {
        lock (_gate)
        {
            _announcements.Add(sentence);
        }

        Announced?.Invoke(sentence);
    }
}
