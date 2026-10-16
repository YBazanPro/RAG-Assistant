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
    private int? _throwAfter;

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => [.. _calls];

    public int CallCount => _calls.Count;

    /// <summary>The next reply streams these fragments, in order.</summary>
    public void Script(params string[] tokens) => _replies.Enqueue(tokens);

    /// <summary>The next calls wait before their first token until <see cref="Release"/>.</summary>
    public void HoldBeforeFirstToken() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilHeldAsync() => _held.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public void Release() => _gate?.TrySetResult();

    /// <summary>The next calls throw a transport failure after streaming <paramref name="tokens"/> fragments.</summary>
    public void ThrowAfter(int tokens) => _throwAfter = tokens;

    /// <summary>Completes when a call observed its cancellation token.</summary>
    public Task CancellationObserved => _cancelled.Task;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The answer pipeline only streams.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _calls.Enqueue([.. messages]);
        var tokens = _replies.TryDequeue(out var scripted) ? scripted : DefaultReply;
        using var registration = cancellationToken.Register(() => _cancelled.TrySetResult());

        if (_gate is { } gate)
        {
            _held.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            if (_throwAfter == i)
            {
                throw new HttpRequestException("Scripted model failure.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, tokens[i]) { ModelId = ModelId };
        }

        if (_throwAfter is { } after && after >= tokens.Length)
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
