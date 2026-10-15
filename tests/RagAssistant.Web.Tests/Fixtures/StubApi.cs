using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core.Interfaces;

namespace RagAssistant.Web.Tests.Fixtures;

public sealed record EchoedRequest(string Method, string Path, string? Authorization, string? Cookie, string? RequestedWith);

/// <summary>A real Kestrel server standing in for the API, so streaming, cancellation and outages behave as on the wire.</summary>
public sealed class StubApi : IAsyncInitializer, IAsyncDisposable
{
    private WebApplication? _app;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _paths = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _authorizations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> _ingestionReleases = new();
    private TaskCompletionSource _streamCancelled = NewSignal();

    public string Url { get; private set; } = "";

    // Tests run in parallel against this shared stub, so assert on a path no other test uses rather than on a counter.
    public bool Received(string path) => _paths.Contains(path);

    /// <summary>The Authorization header the stub saw for a path (null when it was never requested).</summary>
    public string? AuthorizationFor(string path) => _authorizations.GetValueOrDefault(path);

    /// <summary>Lets the ingestion stream of one document send its second event and finish.</summary>
    public void ReleaseIngestion(Guid documentId) => _ingestionReleases.GetOrAdd(documentId, _ => NewSignal()).TrySetResult();

    public Task StreamCancelled => _streamCancelled.Task;

    public void ResetStreamSignal() => _streamCancelled = NewSignal();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.Use((context, next) =>
        {
            _paths.Enqueue(context.Request.Path.Value ?? "");
            _authorizations[context.Request.Path.Value ?? ""] = context.Request.Headers.Authorization.ToString();
            return next();
        });

        app.MapMethods("/api/echo/{**rest}", ["GET", "POST", "PUT", "PATCH", "DELETE"], (HttpRequest request) =>
            Results.Json(new EchoedRequest(
                request.Method,
                request.Path + request.QueryString.Value,
                request.Headers.Authorization.ToString(),
                request.Headers.Cookie.ToString(),
                request.Headers["X-Requested-With"].ToString())));

        app.MapGet("/api/stream", StreamAsync);

        // The Documents read routes, with just enough JSON to prove the BFF forwards them and the client can read the shapes.
        app.MapGet("/api/documents", () => Results.Json(new { items = Array.Empty<object>(), page = 1, pageSize = 25, totalCount = 0 }));
        app.MapGet("/api/documents/facets", () => Results.Json(new { owners = Array.Empty<object>(), languages = Array.Empty<object>(), tags = Array.Empty<object>() }));
        app.MapGet("/api/documents/{id:guid}/versions", () => Results.Json(new { items = Array.Empty<object>() }));
        app.MapGet("/api/documents/{id:guid}/versions/{number:int}/passages", (int number) =>
            Results.Json(new { versionNumber = number, versionStatus = "indexed", isActive = true, items = Array.Empty<object>(), page = 1, pageSize = 50, totalCount = 0 }));
        app.MapGet("/api/documents/{id:guid}/ingestion/events", IngestionEventsAsync);
        app.MapGet("/api/problem", () => Results.Problem(statusCode: 404, title: "Not found", extensions: new Dictionary<string, object?> { ["code"] = "stub.not_found" }));
        app.MapGet("/api/hang", async (CancellationToken ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Results.Ok();
        });

        await app.StartAsync();
        _app = app;
        Url = app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task StreamAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(i)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(250, context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            _streamCancelled.TrySetResult();
        }
    }

    // First event at once, the second only after the test releases it: receiving the first proves the BFF does not buffer.
    private async Task IngestionEventsAsync(Guid id, HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        var release = _ingestionReleases.GetOrAdd(id, _ => NewSignal());
        try
        {
            await context.Response.WriteAsync(
                "id: 1\nevent: ingestion.stage\ndata: {\"stage\":\"extracting\",\"status\":\"started\"}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(
                "id: 2\nevent: ingestion.stage\ndata: {\"stage\":\"completed\",\"status\":\"completed\"}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client went away; nothing to clean up.
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
