using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace RagAssistant.Web.Client.Chat;

public sealed class ClipboardUnavailableException(Exception inner)
    : Exception("The browser did not allow copying to the clipboard.", inner);

/// <summary>Copies text with <c>navigator.clipboard</c>. A seam so component tests need no browser.</summary>
public interface IClipboard
{
    /// <exception cref="ClipboardUnavailableException">The browser blocked the write.</exception>
    Task CopyAsync(string text, CancellationToken cancellationToken);
}

/// <summary>The scrolling and focus work the chat page needs that Blazor cannot do itself.</summary>
public interface IThreadScroller
{
    /// <summary>Moves focus to the element with this id, if it still exists.</summary>
    Task FocusAsync(string elementId, CancellationToken cancellationToken);

    /// <summary>Starts reporting whether the reader is at the bottom of the thread (within ~64 px).</summary>
    Task WatchAsync(ElementReference thread, Func<bool, Task> onFollowingChanged);

    /// <summary>Scrolls to the bottom. Without <paramref name="force"/> only when the reader is following the bottom.</summary>
    Task ScrollToBottomAsync(ElementReference thread, bool force);

    Task UnwatchAsync(ElementReference thread);
}

/// <summary>
/// Sends on Enter, adds a line on Shift+Enter, and ignores Enter during an IME composition. The key must be intercepted
/// before the textarea handles it, which Blazor's event model cannot do conditionally.
/// </summary>
public interface IEnterKeyHandler
{
    Task AttachAsync(ElementReference container, Func<Task> onEnter);

    Task DetachAsync(ElementReference container);
}

/// <summary>The question draft, kept in <c>sessionStorage</c> so it survives a sign-in round trip. Failures are swallowed: storage can be blocked.</summary>
public interface IDraftStore
{
    Task<string?> LoadAsync(string key);

    Task SaveAsync(string key, string text);

    Task ClearAsync(string key);
}

/// <summary>Reports whether the viewport is narrower than 1024 px (the list collapses and the preview becomes a drawer).</summary>
public interface IViewport
{
    /// <summary>Calls back at once with the current state, and again whenever it changes.</summary>
    Task WatchNarrowAsync(Func<bool, Task> onChanged);
}

/// <summary>Keeps Tab inside a modal drawer.</summary>
public interface IFocusTrap
{
    Task AttachAsync(ElementReference container);

    Task DetachAsync(ElementReference container);
}

/// <summary>The browser implementation of all of them: one lazily imported ES module, <c>wwwroot/js/chat.js</c>.</summary>
public sealed class BrowserChatInterop(IJSRuntime js) : IClipboard, IThreadScroller, IEnterKeyHandler, IDraftStore, IViewport, IFocusTrap, IAsyncDisposable
{
    // Relative to the document base ("/"); the module is a static web asset of this project.
    private const string ModulePath = "./js/chat.js";

    private IJSObjectReference? _module;
    private readonly Dictionary<ElementReference, IDisposable> _callbacks = [];
    private DotNetObjectReference<ViewportCallback>? _viewport;

    public async Task CopyAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var module = await ModuleAsync();
            await module.InvokeVoidAsync("copyText", cancellationToken, text);
        }
        catch (JSException ex)
        {
            throw new ClipboardUnavailableException(ex);
        }
    }

    public async Task FocusAsync(string elementId, CancellationToken cancellationToken) =>
        await (await ModuleAsync()).InvokeVoidAsync("focusById", cancellationToken, elementId);

    public async Task WatchAsync(ElementReference thread, Func<bool, Task> onFollowingChanged)
    {
        var callback = DotNetObjectReference.Create(new FollowCallback(onFollowingChanged));
        Remember(thread, callback);
        await (await ModuleAsync()).InvokeVoidAsync("watchThread", thread, callback);
    }

    public async Task ScrollToBottomAsync(ElementReference thread, bool force) =>
        await (await ModuleAsync()).InvokeVoidAsync("scrollToBottom", thread, force);

    public async Task UnwatchAsync(ElementReference thread)
    {
        await SafeAsync(m => m.InvokeVoidAsync("unwatchThread", thread));
        Forget(thread);
    }

    public async Task AttachAsync(ElementReference container, Func<Task> onEnter)
    {
        var callback = DotNetObjectReference.Create(new EnterCallback(onEnter));
        Remember(container, callback);
        await (await ModuleAsync()).InvokeVoidAsync("attachEnter", container, callback);
    }

    public async Task DetachAsync(ElementReference container)
    {
        await SafeAsync(m => m.InvokeVoidAsync("detachEnter", container));
        Forget(container);
    }

    public async Task WatchNarrowAsync(Func<bool, Task> onChanged)
    {
        _viewport?.Dispose();
        _viewport = DotNetObjectReference.Create(new ViewportCallback(onChanged));
        await (await ModuleAsync()).InvokeVoidAsync("watchNarrow", _viewport);
    }

    async Task IFocusTrap.AttachAsync(ElementReference container) =>
        await SafeAsync(m => m.InvokeVoidAsync("trapFocus", container));

    async Task IFocusTrap.DetachAsync(ElementReference container) =>
        await SafeAsync(m => m.InvokeVoidAsync("releaseFocus", container));

    public async Task<string?> LoadAsync(string key) =>
        await SafeAsync(async m => await m.InvokeAsync<string?>("readDraft", key));

    public async Task SaveAsync(string key, string text) => await SafeAsync(m => m.InvokeVoidAsync("writeDraft", key, text));

    public async Task ClearAsync(string key) => await SafeAsync(m => m.InvokeVoidAsync("clearDraft", key));

    // Imported on first use, which only happens after the page is interactive: never while it is prerendered.
    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);

    // Interop that is allowed to fail: the page or its storage is gone, or blocked.
    private async Task SafeAsync(Func<IJSObjectReference, ValueTask> call)
    {
        try
        {
            await call(await ModuleAsync());
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Nothing to do: the caller treats these operations as best effort.
        }
    }

    private async Task<T?> SafeAsync<T>(Func<IJSObjectReference, Task<T?>> call)
    {
        try
        {
            return await call(await ModuleAsync());
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            return default;
        }
    }

    private void Remember(ElementReference element, IDisposable callback)
    {
        Forget(element);
        _callbacks[element] = callback;
    }

    private void Forget(ElementReference element)
    {
        if (_callbacks.Remove(element, out var existing))
        {
            existing.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var callback in _callbacks.Values)
        {
            callback.Dispose();
        }

        _callbacks.Clear();
        _viewport?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit or page is already gone.
            }
        }
    }

    private sealed class FollowCallback(Func<bool, Task> handler)
    {
        [JSInvokable]
        public Task OnFollowingChanged(bool following) => handler(following);
    }

    private sealed class ViewportCallback(Func<bool, Task> handler)
    {
        [JSInvokable]
        public Task OnChanged(bool narrow) => handler(narrow);
    }

    private sealed class EnterCallback(Func<Task> handler)
    {
        [JSInvokable]
        public Task OnEnter() => handler();
    }
}
