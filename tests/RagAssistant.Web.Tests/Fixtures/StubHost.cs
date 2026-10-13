using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core.Interfaces;

namespace RagAssistant.Web.Tests.Fixtures;

public sealed record EchoedRequest(string Method, string Path, string? Authorization, string? Cookie, string? RequestedWith);

/// <summary>A real Kestrel server standing in for the Host, so streaming, cancellation and outages behave as on the wire.</summary>
public sealed class StubHost : IAsyncInitializer, IAsyncDisposable
{
    private WebApplication? _app;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _paths = new();
    private TaskCompletionSource _streamCancelled = NewSignal();

    public string Url { get; private set; } = "";

    // Tests run in parallel against this shared stub, so assert on a path no other test uses rather than on a counter.
    public bool Received(string path) => _paths.Contains(path);

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

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
