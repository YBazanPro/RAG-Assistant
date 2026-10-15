---
name: sse-endpoints
description: How Server-Sent Events endpoints are written and tested in RagAssistant (ingestion progress today, answer streaming next). Use when adding or changing an SSE endpoint, its event DTOs or its tests, or when wiring a client to one.
---

# SSE endpoints in RagAssistant

The reference implementation is `src/Modules/Documents/RagAssistant.Modules.Documents/Features/StreamIngestionEvents/` (spec 007). Copy its shape; the chat answer stream (Phase 3) should look the same.

## Shape of an endpoint

- A feature folder with a static class (`StreamIngestionEvents`) holding the logic and an `IEndpointGroup` (`…Endpoint`) that maps the route. The endpoint reads from the module's `DbContext` directly: a stream is not a request/response pair, so it does not go through the Mediator.
- **Decide errors before the stream starts.** Resolve the target (`ResolveAsync` returns `Result<Target>`) first, then `target.ToHttpResult(t => TypedResults.ServerSentEvents(...))`. A missing document is a normal 404 ProblemDetails; once the first byte of a stream is sent the status cannot change.
- Authorization is the same policy attribute as any endpoint (`RequireAuthorization(PolicyNames.…)`); 401/403 happen before the stream too.
- Produces metadata: `.Produces(StatusCodes.Status200OK, contentType: "text/event-stream")`.

## Events

- **Event type names** are dotted, lowercase, domain first: `ingestion.stage`. A keep-alive uses the type `heartbeat`.
- **Event id** is a monotonic id that already exists in the data (here the `IngestionEvent.Id` identity). Never invent ids that are not persisted; `Last-Event-ID` resume only works when the id means something in storage.
- **Data** is a JSON record (`StageEvent`) with camelCase names (`JsonSerializerDefaults.Web`) and lowercase string values for enums (`"stage":"embedding"`). Serialize yourself and yield `SseItem<string>`: `TypedResults.ServerSentEvents` writes a `string` payload as is, which also lets a heartbeat carry truly empty data.
- Event DTOs are content-free where the log is (no document text); they are an API, so keep them small and additive.
- **Progress inside a stage** is the same event type with `status: "progress"` and nullable `current` / `total` (spec 010): added to the DTO and the table, not a new event type. Every other event carries `null` for both. Clients must treat an unknown `status` as "refine the current stage", never as terminal; only `failed` and `completed/completed` end a run. Throttle what you persist (`ProgressTracker`: `floor(done * 10 / total)` grows, never at the end, so at most nine per stage), because every event is a row that every reader replays.

## Reading and tailing

- Header: `[FromHeader(Name = "Last-Event-ID")] string?`, parsed with `long.TryParse(..., NumberStyles.None)`; anything unparseable means "from the start", never an error.
- Tail by polling the persisted log (`ProgressPollInterval`), not by an in-memory pub/sub: the log survives restarts and works with several instances. Use `Task.Delay(interval, TimeProvider, ct)`.
- **Read the run status first, then the events.** A terminal status is committed together with its last event, so a stream that sees "terminal" and then reads no newer events can safely end.
- End the stream right after sending the terminal event (`Completed/Completed` or any `Failed`); a stream for a run that is already terminal replays and closes.
- Send a `heartbeat` (no id, empty data) every `HeartbeatInterval` while idle, so proxies and the BFF keep the connection open while a worker waits for a model.
- The `CancellationToken` of the request ends the loop; a disconnect must change no state.

## BFF and client

- The BFF forwards `/api/**` through YARP as a pass-through with no response buffering (spec 005), so no BFF code is needed per stream.
- Browser side (Blazor WebAssembly): streaming reads need `request.SetBrowserResponseStreamingEnabled(true)` (`Microsoft.AspNetCore.Components.WebAssembly.Http`) and `HttpCompletionOption.ResponseHeadersRead`; the body is then parsed with `SseParser.Create(stream).EnumerateAsync(ct)`. `IngestionEventSource` (`Web.Client/Documents/IngestionWatcher.cs`) is the one implementation.
- Keep reconnect logic out of the component: `IngestionWatcher` owns it (3 reconnects after 1 s, 2 s, 4 s, resuming with `Last-Event-ID`; 401 = session expired; 403/404 = give up; heartbeats and unknown event types ignored; the counter resets after a received stage event). It takes an `IIngestionEventSource` and a `TimeProvider`, so tests script events and failures and move the clock by hand.
- A non-2xx answer is thrown as `IngestionStreamException(status)` before any event is read; only owners open the stream (spec 007), seekers get a one-time status and a Refresh button.
- The BFF needs nothing per stream. `tests/RagAssistant.Web.Tests/Documents/DocumentsBffTests.cs` proves it: the stub API holds the second event until released, and the client sees the first one first.
- Dispose cancels the token, which closes the HTTP response; the component awaits the watcher task so no callback runs after disposal.

## Testing

Integration tests (`tests/RagAssistant.Integration.Tests/Documents/IngestionStreamTests.cs`):

- `client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)`, then `SseParser.Create(stream).EnumerateAsync(ct)`; always pass a timeout `CancellationToken` so a bug hangs for seconds, not forever.
- Stop reading with a predicate (`stopWhen`) for open-ended streams (heartbeats, disconnect); read to the end for streams that terminate.
- To observe a run in progress, hold the fake model (`Generator.HoldBatches()` / `WaitUntilHeldAsync()` / `Release()`), connect, then release.
- Compare event ids with the ids in the table, not with counts alone.
- A test of the order of `Started` / `Completed` must ignore `Progress` rows (they may appear between them, or not at all for a small document); a test of progress asserts they sit between the stage's `Started` and `Completed`, increase strictly and stay below the total (`IngestionProgressTests`).
- The parser returns the heartbeat as an item with `EventType == "heartbeat"` and empty `Data`.
- Use the `IngestionHarness` (own database, fast intervals) so a worker never sees another test's runs.
