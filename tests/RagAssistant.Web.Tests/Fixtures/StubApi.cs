using System.Security.Cryptography;
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _firstUploadBytes = new();
    private TaskCompletionSource _streamCancelled = NewSignal();

    /// <summary>The bytes of the file served for version 1 of any document (range requests work on it).</summary>
    public static readonly byte[] SampleFile = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

    /// <summary>Completes when the stub has read the first bytes of an upload that carried this <c>X-Test-Id</c>.</summary>
    public Task FirstUploadBytes(string testId) => _firstUploadBytes.GetOrAdd(testId, _ => NewSignal()).Task;

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
        // The real API accepts uploads up to 100 MiB; the BFF's limits are what the upload tests exercise.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
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
        // The lifecycle routes (spec 009): the stub reports the conditional-request header it received so the test can prove the BFF forwards it.
        app.MapPut("/api/documents/{id:guid}", (HttpRequest request, HttpResponse response) =>
        {
            response.Headers.ETag = "\"2\"";
            return Results.Json(new { ifMatch = request.Headers.IfMatch.ToString(), requestedWith = request.Headers["X-Requested-With"].ToString() });
        });
        app.MapPost("/api/documents/{id:guid}/archive", () => Results.Json(new { status = "archived" }));
        app.MapPost("/api/documents/{id:guid}/restore", () => Results.Json(new { status = "active" }));
        app.MapPost("/api/documents/{id:guid}/reprocess", () => Results.Accepted());
        // Uploads are read as a stream and answered with the length and SHA-256 seen, so a test can prove the body arrived intact.
        app.MapPost("/api/documents", (HttpRequest request) => ReadUploadAsync(request));
        app.MapPost("/api/documents/{id:guid}/versions", (Guid id, HttpRequest request) => ReadUploadAsync(request));
        // Version 1 is a plain file with range support; version 2 streams a first chunk and then waits for the test.
        app.MapGet("/api/documents/{id:guid}/versions/{number:int}/file", FileAsync);
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

    private async Task<IResult> ReadUploadAsync(HttpRequest request)
    {
        var testId = request.Headers["X-Test-Id"].ToString();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            if (length == 0 && testId.Length > 0)
            {
                _firstUploadBytes.GetOrAdd(testId, _ => NewSignal()).TrySetResult();
            }

            length += read;
            hash.AppendData(buffer, 0, read);
        }

        return Results.Json(
            new { length, sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()), contentType = request.ContentType },
            statusCode: StatusCodes.Status202Accepted);
    }

    private async Task<IResult> FileAsync(Guid id, int number, HttpContext context)
    {
        if (number == 1)
        {
            // Like the real API, which sets the header itself; the BFF must not add a second one.
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(new MemoryStream(SampleFile), "application/pdf", enableRangeProcessing: true);
        }

        context.Response.ContentType = "application/pdf";
        var release = _ingestionReleases.GetOrAdd(id, _ => NewSignal());
        try
        {
            await context.Response.Body.WriteAsync(SampleFile.AsMemory(0, 1024), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(SampleFile.AsMemory(1024), context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client went away.
        }

        return Results.Empty;
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
