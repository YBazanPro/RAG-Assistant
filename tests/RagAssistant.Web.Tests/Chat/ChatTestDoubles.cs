using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;

namespace RagAssistant.Web.Tests.Chat;

public sealed class FakeClipboard : IClipboard
{
    public List<string> Copied { get; } = [];

    public bool Blocked { get; set; }

    public Task CopyAsync(string text, CancellationToken cancellationToken)
    {
        if (Blocked)
        {
            throw new ClipboardUnavailableException(new InvalidOperationException("blocked"));
        }

        Copied.Add(text);
        return Task.CompletedTask;
    }
}

public sealed class FakeThreadScroller : IThreadScroller
{
    private Func<bool, Task>? _onFollowing;

    public List<string> Focused { get; } = [];

    public List<bool> Scrolls { get; } = [];

    public Task WatchAsync(ElementReference thread, Func<bool, Task> onFollowingChanged)
    {
        _onFollowing = onFollowingChanged;
        return Task.CompletedTask;
    }

    public Task ScrollToBottomAsync(ElementReference thread, bool force)
    {
        Scrolls.Add(force);
        return Task.CompletedTask;
    }

    public Task UnwatchAsync(ElementReference thread) => Task.CompletedTask;

    /// <summary>What the browser reports when the reader scrolls away from, or back to, the bottom.</summary>
    public Task ReportFollowingAsync(bool following) => _onFollowing!(following);

    public Task FocusAsync(string elementId, CancellationToken cancellationToken)
    {
        Focused.Add(elementId);
        return Task.CompletedTask;
    }
}

public sealed class FakeEnterKey : IEnterKeyHandler
{
    public Func<string?, Task>? OnEnter { get; private set; }

    public Task AttachAsync(ElementReference container, Func<string?, Task> onEnter)
    {
        OnEnter = onEnter;
        return Task.CompletedTask;
    }

    public Task DetachAsync(ElementReference container)
    {
        OnEnter = null;
        return Task.CompletedTask;
    }
}

public sealed class FakeDraftStore : IDraftStore
{
    public Dictionary<string, string> Drafts { get; } = [];

    public Task<string?> LoadAsync(string key) => Task.FromResult(Drafts.TryGetValue(key, out var text) ? text : null);

    public Task SaveAsync(string key, string text)
    {
        if (text.Length == 0)
        {
            Drafts.Remove(key);
        }
        else
        {
            Drafts[key] = text;
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(string key)
    {
        Drafts.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class FakeFocusTrap : IFocusTrap
{
    public int Attached { get; private set; }

    public Task AttachAsync(ElementReference container)
    {
        Attached++;
        return Task.CompletedTask;
    }

    public Task DetachAsync(ElementReference container) => Task.CompletedTask;
}

public sealed class FakeViewport : IViewport
{
    private Func<bool, Task>? _onChanged;

    public bool Narrow { get; set; }

    public Task WatchNarrowAsync(Func<bool, Task> onChanged)
    {
        _onChanged = onChanged;
        return onChanged(Narrow);
    }

    public Task ChangeAsync(bool narrow) => _onChanged!(narrow);
}

internal static class ChatTestData
{
    public static Citation Citation(int marker = 1, string title = "Budget Exception Policy", string? text = "Exceptions above 5000 need approval.") =>
        new(marker, Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Guid.NewGuid(),
            3, title, ["Approval", "Thresholds"], 4, null, text);

    public static BunitContext NewContext(out FakeClipboard clipboard, out FakeThreadScroller scroller)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        clipboard = new FakeClipboard();
        scroller = new FakeThreadScroller();
        ctx.Services.AddSingleton<IClipboard>(clipboard);
        ctx.Services.AddSingleton<IThreadScroller>(scroller);
        ctx.Services.AddSingleton<IEnterKeyHandler>(new FakeEnterKey());
        ctx.Services.AddSingleton<IFocusTrap>(new FakeFocusTrap());
        return ctx;
    }
}
