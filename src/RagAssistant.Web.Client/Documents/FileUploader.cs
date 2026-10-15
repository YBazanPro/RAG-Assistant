using Microsoft.JSInterop;
using RagAssistant.Web.Client.Security;

namespace RagAssistant.Web.Client.Documents;

/// <param name="Url">Relative to the app's base address, for example <c>api/documents</c>.</param>
/// <param name="FileInputSelector">CSS selector of the <c>input type=file</c> that holds the chosen file.</param>
/// <param name="Fields">Form fields sent before the file part; a name may repeat.</param>
/// <param name="Headers">Request headers, for example the BFF's CSRF header.</param>
public sealed record UploadRequest(
    string Url,
    string FileInputSelector,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    IReadOnlyDictionary<string, string> Headers);

public readonly record struct UploadProgress(long Sent, long Total);

/// <summary>What came back from an upload: the HTTP status and body, or one of the codes below when there was no response.</summary>
public sealed record UploadResult(int Status, string? Body)
{
    public const int NoFile = -1;
    public const int AbortedStatus = -2;

    /// <summary>The browser could not reach the server.</summary>
    public bool IsNetworkError => Status == 0;

    public bool IsAborted => Status == AbortedStatus;

    public bool IsMissingFile => Status == NoFile;

    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>Sends a chosen file to the API without reading it into the application (spec 010). A seam so component tests need no browser.</summary>
public interface IFileUploader
{
    /// <summary>Cancelling the token aborts the request in flight; the result then reports <see cref="UploadResult.IsAborted"/>.</summary>
    Task<UploadResult> UploadAsync(UploadRequest request, IProgress<UploadProgress> progress, CancellationToken cancellationToken);
}

/// <summary>The browser implementation: a lazily imported ES module around <c>XMLHttpRequest</c>.</summary>
public sealed class BrowserFileUploader(IJSRuntime js) : IFileUploader, IAsyncDisposable
{
    // Relative to the document base ("/"); the module is a static web asset of this project.
    private const string ModulePath = "./js/upload.js";

    private IJSObjectReference? _module;

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<UploadProgress> progress, CancellationToken cancellationToken)
    {
        // Imported on first use, which only happens after a click: never while the page is prerendered.
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
        var id = Guid.NewGuid().ToString("N");
        using var reporter = DotNetObjectReference.Create(new ProgressSink(progress));
        await using var registration = cancellationToken.Register(() => _ = AbortAsync(id));

        var fields = request.Fields.Select(f => new { name = f.Key, value = f.Value }).ToArray();
        var result = await _module.InvokeAsync<UploadResult>(
            "upload", CancellationToken.None, id, request.FileInputSelector, request.Url, fields, request.Headers, reporter);
        return result;
    }

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

    private async Task AbortAsync(string id)
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("abort", id);
            }
        }
        catch (JSDisconnectedException)
        {
            // Nothing left to abort.
        }
    }

    private sealed class ProgressSink(IProgress<UploadProgress> progress)
    {
        [JSInvokable]
        public void Report(long sent, long total) => progress.Report(new UploadProgress(sent, total));
    }
}

/// <summary>The one thing a request needs to say which header the BFF wants; kept here so callers do not repeat the strings.</summary>
public static class UploadHeaders
{
    public static IReadOnlyDictionary<string, string> Default { get; } =
        new Dictionary<string, string> { [RequestedWithHandler.HeaderName] = RequestedWithHandler.HeaderValue };
}
