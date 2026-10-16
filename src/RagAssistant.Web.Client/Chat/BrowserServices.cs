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

/// <summary>The few DOM operations the chat page needs that Blazor cannot do itself.</summary>
public interface IThreadScroller
{
    /// <summary>Moves focus to the element with this id, if it still exists.</summary>
    Task FocusAsync(string elementId, CancellationToken cancellationToken);
}

/// <summary>The browser implementation of both: one lazily imported ES module, <c>wwwroot/js/chat.js</c>.</summary>
public sealed class BrowserChatInterop(IJSRuntime js) : IClipboard, IThreadScroller, IAsyncDisposable
{
    // Relative to the document base ("/"); the module is a static web asset of this project.
    private const string ModulePath = "./js/chat.js";

    private IJSObjectReference? _module;

    public async Task CopyAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var module = await ModuleAsync(cancellationToken);
            await module.InvokeVoidAsync("copyText", cancellationToken, text);
        }
        catch (JSException ex)
        {
            throw new ClipboardUnavailableException(ex);
        }
    }

    public async Task FocusAsync(string elementId, CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("focusById", cancellationToken, elementId);
    }

    // Imported on first use, which only happens after an interaction: never while the page is prerendered.
    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken cancellationToken) =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);

    public async ValueTask DisposeAsync()
    {
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
}
