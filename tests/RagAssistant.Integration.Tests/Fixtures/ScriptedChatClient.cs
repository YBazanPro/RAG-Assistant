using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RagAssistant.Integration.Tests.Fixtures;

/// <summary>
/// Deterministic stand-in for the chat model (spec 012 tests). Each call streams the next scripted reply token by token; without a
/// script it answers <see cref="DefaultReply"/>. It records every prompt, can hold before its first token, and can fail at a chosen point.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    public const string ModelId = "fake-chat";

    public static readonly string[] DefaultReply = ["Department head ", "and Finance [1]."];

    private readonly ConcurrentQueue<string[]> _replies = [];
    private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _calls = [];
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _gate;
    private object? _throwAfter;

    // The follow-up rewrite (spec 014) is a separate, non-streaming structured call, scripted on its own.
    private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _rewriteCalls = [];
    private readonly ConcurrentQueue<string> _rewriteReplies = [];
    private readonly TaskCompletionSource _rewriteHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _rewriteCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _rewriteGate;
    private bool _rewriteThrows;
    private bool _rewriteTimesOutInTheClient;

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => [.. _calls];

    public int CallCount => _calls.Count;


    /// <summary>Every rewrite prompt received, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> RewriteCalls => [.. _rewriteCalls];

    /// <summary>The next rewrite replies with this standalone question.</summary>
    public void ScriptRewrite(string standaloneQuestion) =>
        _rewriteReplies.Enqueue(System.Text.Json.JsonSerializer.Serialize(new { standaloneQuestion }));

    /// <summary>The next rewrite replies with this standalone question and an answer language the user asked for (spec 018).</summary>
    public void ScriptRewrite(string standaloneQuestion, string? requestedLanguage) =>
        _rewriteReplies.Enqueue(System.Text.Json.JsonSerializer.Serialize(new { standaloneQuestion, requestedLanguage }));

    /// <summary>The next rewrite replies with this raw text, for malformed answers.</summary>
    public void ScriptRewriteRaw(string text) => _rewriteReplies.Enqueue(text);

    /// <summary>Rewrite calls wait until <see cref="ReleaseRewrite"/> or cancellation.</summary>
    public void HoldRewrite() => _rewriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilRewriteHeldAsync() => _rewriteHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public void ReleaseRewrite() => _rewriteGate?.TrySetResult();

    /// <summary>Completes when a rewrite call observed its cancellation token.</summary>
    public Task RewriteCancellationObserved => _rewriteCancelled.Task;

    /// <summary>Every following rewrite call fails like an HttpClient timeout: a TaskCanceledException that is not our token.</summary>
    public void TimeOutInClientOnRewrite() => _rewriteTimesOutInTheClient = true;

    /// <summary>Every following rewrite call throws a transport failure.</summary>
    public void ThrowOnRewrite() => _rewriteThrows = true;

    /// <summary>The next reply streams these fragments, in order.</summary>
    public void Script(params string[] tokens) => _replies.Enqueue(tokens);

    /// <summary>The next calls wait before their first token until <see cref="Release"/>.</summary>
    public void HoldBeforeFirstToken() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilHeldAsync() => _held.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public void Release() => _gate?.TrySetResult();

    /// <summary>The next call throws a transport failure after streaming <paramref name="tokens"/> fragments.</summary>
    public void ThrowAfter(int tokens) => _throwAfter = tokens;

    /// <summary>Completes when a call observed its cancellation token.</summary>
    public Task CancellationObserved => _cancelled.Task;

    /// <summary>Only the follow-up rewrite uses this call; the answer itself streams. Without a script it returns the current question unchanged.</summary>
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.ToList();
        _rewriteCalls.Enqueue(prompt);

        if (_rewriteGate is { } gate)
        {
            _rewriteHeld.TrySetResult();
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _rewriteCancelled.TrySetResult();
                throw;
            }
        }

        if (_rewriteTimesOutInTheClient)
        {
            throw new TaskCanceledException("Scripted client timeout.");
        }

        if (_rewriteThrows)
        {
            throw new HttpRequestException("Scripted rewrite failure.");
        }

        var text = _rewriteReplies.TryDequeue(out var scripted) ? scripted : Unchanged(prompt);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = ModelId,
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 10 },
        };
    }

    private static string Unchanged(List<ChatMessage> prompt)
    {
        const string Marker = "Current question: ";
        var user = prompt.Last(m => m.Role == ChatRole.User).Text;
        var question = user[(user.LastIndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
        return System.Text.Json.JsonSerializer.Serialize(new { standaloneQuestion = question });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _calls.Enqueue([.. messages]);
        var tokens = _replies.TryDequeue(out var scripted) ? scripted : DefaultReply;
        var throwAfter = (int?)Interlocked.Exchange(ref _throwAfter, null);

        // Recorded where the cancellation is caught: a token registration would be removed before it runs, because the
        // cancelled wait continues inline inside Cancel() and leaves this method first.
        if (_gate is { } gate)
        {
            _held.TrySetResult();
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancelled.TrySetResult();
                throw;
            }
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            if (throwAfter == i)
            {
                throw new HttpRequestException("Scripted model failure.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _cancelled.TrySetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, tokens[i]) { ModelId = ModelId };
        }

        if (throwAfter is { } after && after >= tokens.Length)
        {
            throw new HttpRequestException("Scripted model failure.");
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ModelId = ModelId,
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = tokens.Length })],
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", defaultModelId: ModelId) : null;

    public void Dispose()
    {
    }
}
