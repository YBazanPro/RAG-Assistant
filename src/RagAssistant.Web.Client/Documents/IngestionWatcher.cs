using System.Net.ServerSentEvents;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace RagAssistant.Web.Client.Documents;

/// <summary>One server-sent event as the watcher sees it; <see cref="Id"/> is null for heartbeats.</summary>
public sealed record SseEvent(string Type, string? Id, string Data);

public sealed class IngestionStreamException(int status) : Exception($"The ingestion stream answered {status}.")
{
    public int Status { get; } = status;
}

/// <summary>Opens the ingestion SSE stream. A seam so tests can feed events and failures with controlled timing.</summary>
public interface IIngestionEventSource
{
    IAsyncEnumerable<SseEvent> OpenAsync(Guid documentId, string? lastEventId, CancellationToken cancellationToken);
}

public sealed class IngestionEventSource(Func<HttpClient> httpClient) : IIngestionEventSource
{
    public async IAsyncEnumerable<SseEvent> OpenAsync(
        Guid documentId, string? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/documents/{documentId}/ingestion/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        // Without this the browser's fetch buffers the body and no event arrives before the stream ends.
        request.SetBrowserResponseStreamingEnabled(true);

        using var response = await httpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new IngestionStreamException((int)response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            yield return new SseEvent(item.EventType, string.IsNullOrEmpty(item.EventId) ? null : item.EventId, item.Data);
        }
    }
}

public enum WatchOutcome
{
    /// <summary>A terminal event arrived.</summary>
    Finished,

    /// <summary>The stream could not be kept alive; the user can refresh.</summary>
    Lost,

    /// <summary>The session ended (401).</summary>
    SessionExpired,
}

/// <summary>Follows one document's ingestion events, reconnecting with <c>Last-Event-ID</c> when the stream drops (spec 008).</summary>
public sealed class IngestionWatcher(IIngestionEventSource source, TimeProvider clock)
{
    private static readonly TimeSpan[] ReconnectDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    public async Task<WatchOutcome> WatchAsync(Guid documentId, Func<IngestionStageEvent, Task> onStage, CancellationToken cancellationToken)
    {
        string? lastEventId = null;
        var reconnects = 0;

        while (true)
        {
            try
            {
                await foreach (var item in source.OpenAsync(documentId, lastEventId, cancellationToken))
                {
                    if (item.Type != "ingestion.stage")
                    {
                        continue;
                    }

                    var stage = JsonSerializer.Deserialize<IngestionStageEvent>(item.Data, DocumentsApi.Json);
                    if (stage is null)
                    {
                        continue;
                    }

                    lastEventId = item.Id ?? lastEventId;
                    reconnects = 0;
                    await onStage(stage);
                    if (stage.IsTerminal)
                    {
                        return WatchOutcome.Finished;
                    }
                }
            }
            catch (IngestionStreamException ex) when (ex.Status == 401)
            {
                return WatchOutcome.SessionExpired;
            }
            catch (IngestionStreamException ex) when (ex.Status is 403 or 404)
            {
                return WatchOutcome.Lost;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or IngestionStreamException or JsonException)
            {
                // A dropped or refused stream is retried below; anything else is a bug and must surface.
            }

            if (reconnects >= ReconnectDelays.Length)
            {
                return WatchOutcome.Lost;
            }

            await Task.Delay(ReconnectDelays[reconnects++], clock, cancellationToken);
        }
    }
}
