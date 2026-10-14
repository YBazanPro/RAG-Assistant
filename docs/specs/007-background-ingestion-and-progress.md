# Spec 007: Background ingestion and progress stream

**Status:** Approved (2026-10-14)
**Date:** 2026-10-14
**Roadmap:** steps 1.3 + 1.4 ([roadmap](../roadmap.md)) · **Builds on:** [spec 006](006-documents-data-model-and-import.md) · **Decision records:** [ADR 0003](../adr/0003-model-provider.md), [ADR 0004](../adr/0004-document-versioning.md), [ADR 0005](../adr/0005-api-kernel-libraries.md)

## Problem

Imported documents stay `queued` forever: nothing extracts, chunks or embeds them (spec 006, "Imported document before 1.3 ships"). Until a version is **indexed** it has no passages, so retrieval (Phase 2) and chat (Phase 3) have nothing to work with, and a content owner cannot tell whether an import worked, is still running, or failed. This spec delivers the ingestion pipeline (extract → chunk → embed → store) as a background service, makes failures visible and retryable, and exposes the persisted stage log as a live Server-Sent Events stream, so that a content owner imports a Markdown policy and watches it being indexed.

## Scope

### In

- `IngestionWorker` (`BackgroundService` + `Channel<Guid>` of run ids) in the Documents module, registered by `AddDocumentsModule` (Host only; the MigrationService registers persistence only).
- The pipeline: extract → normalize/structure → heading-aware chunking → batched embedding → single-transaction store, with a persisted stage event for every transition.
- Activation: when a version is indexed it becomes `Document.ActiveVersionId` and the previous active version becomes `Superseded`.
- Failure handling: per-batch retry with backoff, failed runs visible on the document, and a manual retry endpoint that creates a new run.
- Startup recovery: runs left `Queued` or `Running` are re-queued; the persisted run table is the source of truth.
- Embedding provider wiring: `Microsoft.Extensions.AI` abstraction in the module; Ollama container (`bge-m3`) in the AppHost; the Host alone references the Ollama client (ADR 0003).
- `POST /api/documents/{id}/ingestion/retry` and `GET /api/documents/{id}/ingestion/events` (SSE with `Last-Event-ID` resume).
- `GET /api/documents/{id}` gains `latestRun` and `latestVersion.passageCount`.
- Options `Documents:Chunking` and `Documents:Ingestion`, validated on startup.
- Telemetry: counters, a duration histogram, spans, structured logs.
- Integration and unit tests (TUnit, Testcontainers, fake embedding generator).
- Docs: roadmap (1.3, 1.4 done), CLAUDE.md (Documents module line), `sse-endpoints` project skill (roadmap: created after 1.4).

### Out

- Listing, filtering, passages endpoint (1.5); Documents UI (1.6, which also verifies the SSE stream through the BFF).
- Edit metadata, upload new version, archive, reprocess (1.7). The activation and supersede logic is implemented here and reused there.
- PDF/DOCX (1.8); real tokenizer; embedding batch-level progress events; parallel embedding batches.
- Retrieval, `IDocumentSearch`, chat (Phase 2+). Passages are written but nothing reads them yet except tests.
- Admin views of failures, queue depth gauges (Phase 4).

## Domain Model

No new tables and no migration: the schema from spec 006 already holds everything. This spec defines the behaviour of the existing entities.

### Run and version state machine

```text
Import / Retry   Run Queued (stage Queued)      Version Queued
Worker claims    Run Running (StartedAt)        Version Processing
Stages           Extracting → Chunking → Embedding → Storing   (each: Started, Completed events)
Success          Run Succeeded, stage Completed Version Indexed, PassageCount, EmbeddingModel/Dimensions, IndexedAt
                 Document.ActiveVersionId = version; previous active version → Superseded
Failure          Run Failed (FinishedAt, ErrorCode, ErrorMessage; CurrentStage = failing stage)
                 Version Failed; the previous active version stays active
Restart          Run Running → Queued, Version Processing → Queued (same attempt)
Retry            Version Failed → Queued; new Run (Attempt + 1)
```

### Events written (IngestionEvent)

| When | Stage | Status | Message |
|---|---|---|---|
| Import / retry (existing, 006) | `Queued` | `Completed` | `Queued` / `Retry requested` |
| Re-queue after restart | `Queued` | `Started` | `Re-queued after restart` |
| Each stage begins / ends | `Extracting`, `Chunking`, `Embedding`, `Storing` | `Started` / `Completed` | Content-free summary, e.g. `42 passages`, `3 batches` |
| Run succeeded | `Completed` | `Completed` | `Indexed` |
| Any failure | the failing stage | `Failed` | Content-free error message |

The terminal events are `Completed/Completed` and any `Failed`. The final `Storing/Completed` and `Completed/Completed` events are written in the same transaction as the passages and the state changes, so a stream never announces completion before the data is committed. Earlier stage events are committed as they happen, so the stream is live.

### Passage rules

- `Position` is 0-based across the version; `SectionPath` is the heading chain; `SectionOrdinal` is the 0-based order among passages of the version with an equal `SectionPath`.
- `Text` is the stored passage content, including any overlap and raw Markdown syntax (not stripped). It never includes the heading line that is already in `SectionPath`.
- `CharCount = Text.Length`; `TokenEstimate = ceil(CharCount / 4)`; `PageStart/PageEnd = null`.
- A new GUID per passage, per version, never reused (ADR 0004).
- `Embedding` is the model's vector for the **embedding input**: `"<SectionPath joined by ' > '>\n\n<Text>"` when the path is not empty, otherwise `Text`.

### Chunking algorithm (Markdown and plain text)

1. **Structure.** Markdown: ATX headings `#` to `######` outside fenced code define the section chain (a heading of level *n* replaces the chain from level *n* down). Setext headings and HTML blocks are plain paragraphs. Text before the first heading has an empty path. Plain text has no headings.
2. **Blocks.** A section's body is split into blocks separated by blank lines. A fenced code block (``` or ~~~) is one block even if it contains blank lines; a table (consecutive lines starting with `|`) is one block.
3. **Packing.** Blocks are packed in order into passages up to `MaxTokens`. A block is never split unless it alone exceeds `MaxTokens`: it is then split on line boundaries (table pieces repeat the header rows, code pieces are re-wrapped in the opening and closing fence); a single line over the limit is split at whitespace, or at the limit if there is none.
4. **Overlap.** When a section produces more than one passage, each passage after the first starts with the trailing text of the previous one, about `OverlapTokens`, cut on a whitespace boundary. There is no overlap across sections.
5. **Minimum size.** A passage under `MinTokens` is merged into the previous passage if both share the same `SectionPath`, otherwise into the next passage with the same parent path (the path without its last element), keeping the first passage's `SectionPath` and prefixing the later part with its heading line, as long as the result stays within `MaxTokens`. If neither merge is possible it is kept as is: content is never dropped.
6. **Empty sections.** A heading with no body produces no passage (its text still appears in the `SectionPath` of descendants). A version with **zero** passages fails (`documents.no_passages`).

## API Contract

All routes are under `/api` (spec 002), return ProblemDetails with `code` and `traceId` on error.

| Verb | Route | Request | Success | Errors |
|---|---|---|---|---|
| POST | `/api/documents/{documentId:guid}/ingestion/retry` | — | `202 Accepted`, body `RetryIngestion.Response` | 401 · 403 · 404 `documents.not_found` · 409 `documents.not_retryable` |
| GET | `/api/documents/{documentId:guid}/ingestion/events` | header `Last-Event-ID` (optional) | `200 text/event-stream` | 401 · 403 · 404 `documents.not_found` |
| GET | `/api/documents/{documentId:guid}` (extended) | — | `200` `GetDocument.Response` + `latestRun`, `latestVersion.passageCount` | 401 · 404 |

```jsonc
// 202 — RetryIngestion.Response
{ "documentId": "…", "versionId": "…", "ingestionRunId": "…", "attempt": 2, "status": "queued" }

// GetDocument.Response additions
"latestVersion": { …, "passageCount": null },          // set once indexed
"latestRun": { "id": "…", "attempt": 1, "status": "failed", "stage": "embedding",
               "errorCode": "documents.embedding_unavailable", "errorMessage": "…",
               "queuedAt": "…", "startedAt": "…", "finishedAt": "…" }
```

**Retry rules.** Allowed only when the document is `Active` and its latest version is `Failed`; otherwise `409 documents.not_retryable`. In one transaction: version `Failed → Queued` (a conditional update, so two concurrent retries produce one `202` and one `409`), new run `Attempt + 1` `Queued`, event `Queued/Completed` (`Retry requested`); after commit the run id is written to the channel. There is no attempt limit.

**SSE stream.** Event type `ingestion.stage`, SSE `id` = `IngestionEvent.Id`, JSON camelCase data:

```text
id: 41
event: ingestion.stage
data: {"runId":"…","versionId":"…","versionNumber":1,"attempt":1,"stage":"embedding","status":"started","message":"3 batches","occurredAt":"…"}
```

- The stream targets the **latest run** of the document's **latest version**. It first replays that run's events with `Id > Last-Event-ID` (all of them if the header is absent or not a number), then tails the log (polling `ingestion_events` every `ProgressPollInterval`), and ends after sending the terminal event. A run already terminal with nothing newer than `Last-Event-ID` yields an empty stream that closes at once.
- Event ids are global and monotonic, so a client holding an id from an older run receives the whole latest run after a retry.
- While idle, a `heartbeat` event (no id, empty data) is sent every `HeartbeatInterval` so proxies keep the connection open, for example while the worker waits for the model.
- Implemented with `TypedResults.ServerSentEvents` and typed event DTOs; client disconnect cancels the loop and changes no state.

## Authorization

| Endpoint | Policy | Knowledge Seeker | Content Owner | Maintainer | Anonymous |
|---|---|---|---|---|---|
| `POST …/ingestion/retry` | `ContentOwner` | 403 | 202 | 202 | 401 |
| `GET …/ingestion/events` | `ContentOwner` | 403 | 200 | 200 | 401 |
| `GET /api/documents/{id}` | `KnowledgeSeeker` | 200 | 200 | 200 | 401 |

Seekers see failure state only through the derived `status` and `latestRun` on `GET /api/documents/{id}`; messages are content-free. The worker runs as the system: it never writes `UpdatedBy`/`CreatedBy`, and ingestion does not change `Document.UpdatedAt`.

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| Embedding model not reachable or not yet pulled (fresh clone) | Before starting each run the worker probes the generator with one tiny call; while it fails, the run stays `Queued` and the probe repeats every `ProbeInterval`. Nothing fails; one warning per pause is logged. |
| Transient failure mid-run (timeout, connection error) | Each batch is retried up to `EmbeddingMaxRetries` (3) times with exponential backoff from `EmbeddingRetryBaseDelay`, each call limited by `EmbeddingTimeout`. Still failing → run `Failed`, `documents.embedding_unavailable`. |
| Provider returns the wrong number of vectors or a vector whose length ≠ `Documents:Embeddings:Dimensions` | Immediate `Failed`, `documents.embedding_dimension_mismatch`, no retry. |
| Version yields zero passages | `Failed`, `documents.no_passages`. |
| Unexpected exception in a stage | `Failed`, `documents.ingestion_error`, message `Unexpected error; see logs`; the exception is logged. |
| Host stops during a run | Nothing is written on cancellation; the run stays `Running` and is re-queued at the next start. Passages are stored only in the final transaction, so no partial passages exist. |
| Crash between import commit and channel write | The run is `Queued` in the table; the startup re-queue picks it up. |
| `MaxConcurrentRuns > 1` | N consumers read the channel; a run is claimed with a conditional update `Queued → Running`, so one run is never processed twice. FIFO by `QueuedAt` at start-up. |
| Retry while a retry is already queued/running | `409 documents.not_retryable` (latest version is not `Failed`). |
| Retry on a version that failed because of content (e.g. `no_passages`) | Accepted; it fails the same way. No automatic loop. |
| SSE for an unknown document | `404` before any stream. |
| SSE reconnect with an invalid `Last-Event-ID` | Treated as absent: full replay of the latest run. |
| SSE connection open while the worker is paused | Stays open, heartbeats only, until the run progresses. |
| Superseded version | Passages and embeddings are kept (history, ADR 0004); only the active version is retrievable. |
| Embedding input longer than the model context | Not possible: `MaxTokens` (400, estimate) is far below `bge-m3`'s 8k context; the estimate error is accepted. |
| Document archived before its run starts | Archive arrives in 1.7; the 1.7 spec decides. Here every document is `Active`. |

## Non-Functional Requirements

- **Ingestion time:** a typical document (≤ 50 KB) is indexed in < 1 minute on a dev machine with warm Ollama (ADR 0004 target). Not enforced by a test; measured by hand and by Phase 5.
- **Progress latency:** a stage event reaches an open stream within `ProgressPollInterval` (default 500 ms) plus network.
- **Memory:** a version's content (≤ 1 MB) and its passages are held in memory for one run per consumer.
- **Configuration** (all `ValidateOnStart`, all > 0, `OverlapTokens < MaxTokens`, `MinTokens < MaxTokens`):

| Key | Default |
|---|---|
| `Documents:Chunking:MaxTokens` / `OverlapTokens` / `MinTokens` | 400 / 50 / 80 |
| `Documents:Ingestion:MaxConcurrentRuns` | 1 |
| `Documents:Ingestion:EmbeddingBatchSize` | 16 |
| `Documents:Ingestion:EmbeddingTimeout` | 60 s |
| `Documents:Ingestion:EmbeddingMaxRetries` / `EmbeddingRetryBaseDelay` | 3 / 2 s |
| `Documents:Ingestion:ProbeInterval` | 15 s |
| `Documents:Ingestion:ProgressPollInterval` / `HeartbeatInterval` | 500 ms / 15 s |

- **Time and delays:** all waits (backoff, probe, poll, heartbeat) use the injected `TimeProvider`, so tests control them.
- **Logging:** `LoggerMessage` entries with run id, version id, stage, attempt, outcome; never document or passage text.
- **Telemetry:** `Meter("RagAssistant.Documents")`: counters `documents.ingestion.completed`, `documents.ingestion.failed` (tag `reason` = error code), `documents.passages.created`; histogram `documents.ingestion.duration` (seconds, run start to finish). `ActivitySource("RagAssistant.Documents")`: span `ingestion.run` with a child span per stage, registered through ServiceDefaults like the meter.
- **Architecture:** the module references `Microsoft.Extensions.AI.Abstractions` only; no provider SDK, no reference to another module; the Host registers the generator. `arch-check` passes.
- **Dependencies:** `Microsoft.Extensions.AI.Abstractions` (module, named in CLAUDE.md), `CommunityToolkit.Aspire.Hosting.Ollama` (AppHost) and `CommunityToolkit.Aspire.OllamaSharp` (Host), the latter two accepted by ADR 0003. No resilience library: the retry loop is hand-written. Versions go in `Directory.Packages.props`.

## Integrations

- **Ollama (AppHost):** persistent container (`ContainerLifetime.Persistent`) with the named volume `ragassistant-ollama-models` and the `bge-m3` model, so the download happens once. The Host receives the connection by reference and does **not** wait for the model: ingestion pauses until the model is ready (Edge Cases). The model name is configuration (`Documents:Embeddings:Model`) and must match the model the AppHost pulls (keep-in-sync comment, as for the Postgres tag).
- **Host:** registers `IEmbeddingGenerator<string, Embedding<float>>` from the Ollama client. Modules never see the provider.
- **PostgreSQL:** same schema; adds bulk inserts of passages (batched `AddRange`, one transaction) and `ExecuteUpdate` for claims.
- **BFF:** no change; `/api/**` is a pass-through with no response buffering (spec 005). Streaming through the BFF is verified when the UI consumes it (1.6).
- **Test infrastructure:** `RagAssistantAppFactory` replaces the generator with a deterministic fake; see Acceptance Criteria.
- **Other modules / contracts:** none.

## Acceptance Criteria

Pipeline

- **AC-1** Given an imported Markdown document, when the worker runs with a working generator, then the run becomes `Succeeded` (stage `Completed`), the version `Indexed` with `PassageCount`, `EmbeddingModel`, `EmbeddingDimensions` and `IndexedAt`, `Document.ActiveVersionId` is the version, and every passage has an embedding of the configured dimension.
- **AC-2** Given AC-1, then the event log contains, in order: `Queued/Completed`, then `Started` and `Completed` for `Extracting`, `Chunking`, `Embedding`, `Storing`, then `Completed/Completed`; ids are strictly increasing.
- **AC-3** Given a document with a second indexed version, when the second is indexed, then `ActiveVersionId` is the second version, the first is `Superseded` and its passages remain.
- **AC-4** Given a plain-text file, when ingested, then passages have empty `SectionPath` and are split on paragraphs.
- **AC-5** Given a `.md` file with nested headings, then `SectionPath` equals the heading chain, headings are not in `Text`, `SectionOrdinal` restarts per path, and `Position` is 0-based and gapless.
- **AC-6** Given a section larger than `MaxTokens`, then it yields several passages each ≤ `MaxTokens`, and each after the first starts with the tail of the previous one; given two sections, then there is no overlap between them.
- **AC-7** Given a fenced code block and a table inside a section, then neither is split across passages; given one over `MaxTokens`, then it is split on line boundaries (table header repeated, code re-fenced).
- **AC-8** Given a passage under `MinTokens`, then it is merged per the minimum-size rule within `MaxTokens`; if no merge is possible it is kept, and the concatenated passage texts contain every non-heading body line of the source.
- **AC-9** Given a passage with a non-empty `SectionPath`, then the text sent to the generator is `"A > B\n\n<Text>"`; with an empty path it is `Text` only.
- **AC-10** Given a document of more passages than `EmbeddingBatchSize`, then the generator is called in batches of at most that size.

Failure and retry

- **AC-11** Given a generator that fails twice then succeeds on a batch, then the run succeeds (retry with backoff; with a controlled `TimeProvider` no real delay).
- **AC-12** Given a generator that always fails, then after `EmbeddingMaxRetries` retries the run is `Failed` with `documents.embedding_unavailable`, stage `Embedding`, a `Failed` event, the version `Failed`, no passages stored and the previous active version still active.
- **AC-13** Given a generator returning vectors of the wrong length or count, then `Failed` with `documents.embedding_dimension_mismatch` after one call.
- **AC-14** Given a file with only headings, then `Failed` with `documents.no_passages`.
- **AC-15** Given a failed latest version, when a ContentOwner `POST`s retry, then `202`, the version is `Queued`, a new run with `Attempt = 2` exists, and with a working generator it ends `Indexed`.
- **AC-16** Given a latest version that is not `Failed` (queued, processing or indexed), when retry is called, then `409 documents.not_retryable`; given two concurrent retries on a failed version, then exactly one `202` and one `409`; given an unknown id, then `404`.
- **AC-17** Given a stage that throws an unexpected exception, then `Failed` with `documents.ingestion_error` and a content-free message.

Model readiness and recovery

- **AC-18** Given a generator that is unavailable at the probe, then the run stays `Queued` and not failed; when the generator becomes available, then the run is processed and succeeds.
- **AC-19** Given runs left `Running` (and `Queued`) when the worker starts, then `Running` runs are reset to `Queued` with the same attempt and a `Queued/Started` event, versions `Processing → Queued`, and all are processed in `QueuedAt` order.
- **AC-20** Given `MaxConcurrentRuns = 2` and several queued runs, then each run is processed exactly once.

Progress stream

- **AC-21** Given a ContentOwner connected while a run is in progress, when read with `SseParser`, then `ingestion.stage` events arrive live in order with ids equal to `IngestionEvent.Id`, and the stream ends after the terminal event.
- **AC-22** Given a terminal run, when the stream is opened, then all events are replayed and the stream closes; given `Last-Event-ID` = a mid-run id, then only later events are sent; given the last id, then the stream is empty and closes; given an invalid id, then full replay.
- **AC-23** Given a retry after a failure and a client holding an id from the failed run, then the stream delivers the whole new run.
- **AC-24** Given an idle stream (worker paused), then `heartbeat` events are sent at `HeartbeatInterval` (controlled `TimeProvider`).
- **AC-25** Given an unknown document, then `404` with `documents.not_found` before any stream; given a client disconnect mid-stream, then no state is changed.

Document read

- **AC-26** Given a failed document, when a KnowledgeSeeker calls `GET /api/documents/{id}`, then `status = "failed"` and `latestRun` carries `errorCode`/`errorMessage`; given an indexed one, then `status = "active"`, `activeVersionId` is set and `latestVersion.passageCount` is set.

Authorization

- **AC-27** Given an anonymous caller, then retry and events return `401`; given a KnowledgeSeeker, then both return `403`; given a ContentOwner and a Maintainer, then `202` / `200`.

Configuration, telemetry, architecture

- **AC-28** Given invalid chunking or ingestion options (`OverlapTokens ≥ MaxTokens`, `MinTokens ≥ MaxTokens`, any value ≤ 0), when the Host starts, then startup fails with an options validation error.
- **AC-29** Given a successful and a failed ingestion, then `documents.ingestion.completed`, `documents.ingestion.failed` (tag `reason`), `documents.passages.created` and `documents.ingestion.duration` record the expected values (`MetricCollector<T>`), and an `ingestion.run` activity with stage children is emitted.
- **AC-30** The Documents project references no provider SDK and no other module; the Host registers the generator; the AppHost contains the Ollama resource; `arch-check` passes.

## Testing Approach

- Unit tests (TUnit) for the chunker (AC-4 to AC-9) as pure functions on strings, and for option validation.
- Integration tests run the real `BackgroundService` against Testcontainers PostgreSQL with a **controllable fake** `IEmbeddingGenerator`: deterministic vectors, scriptable failures, and a readiness switch. Tests wait for terminal state by bounded polling or by reading the SSE stream; delays use a controlled `TimeProvider`. No real model is called.
- Because the default fake keeps the worker **paused** (probe fails), imports made by the spec 006 tests stay `queued` and those tests are unchanged. Ingestion tests use a dedicated factory/database with the fake enabled, so parallel tests never see each other's documents indexed. A restart test stops and starts the worker host to prove AC-19.
- The AppHost Ollama wiring is verified by hand (run the AppHost once) and recorded in Implementation Notes.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Batch-level embedding progress events | Stage-level events only | The UI needs a percentage (1.6) |
| Stream of all runs/versions of a document | Latest run only | A history view needs it |
| Retry attempt limit | Unlimited manual retries | A poison document is observed |
| Real tokenizer | `ceil(chars / 4)` estimate | Evaluation shows passages exceeding model limits |
| Setext headings, HTML blocks, Markdown syntax stripping | Plain paragraphs; raw Markdown stored | Evaluation shows noise in retrieval (Phase 5) |
| Parallel embedding batches within a run | Sequential batches | Ingestion exceeds 1 minute on typical files |
| Retention of superseded passages/embeddings | Kept | Storage or index size becomes a problem (1.7 spec) |
| Queue depth / stage metrics, admin failure view | Counters and duration only | Phase 4 |

## Open Questions

None. Resolved in review (2026-10-14): Ollama models live on the named persistent volume `ragassistant-ollama-models`; raw Markdown is kept in passages and embedding input; ingestion tests use a dedicated factory/database; the SSE stream tails by polling.

## Implementation Notes

Choices made while implementing; none changes the approved behavior except where marked **Deviation** (recorded 2026-10-14):

- **Structure:** `Ingestion/` holds `Chunker` (pure), `PassageEmbedder` (batches, retry, probe), `IngestionRunProcessor` (one run, scoped), `IngestionWorker` (hosted service, consumers, startup recovery) and `IngestionQueue` (the `Channel<Guid>`, a wake-up hint only). Retry and the stream are feature slices (`RetryIngestion`, `StreamIngestionEvents`). The stream reads the `DbContext` directly rather than through the Mediator.
- **Claiming:** a run is claimed by a conditional `ExecuteUpdate` (`Queued` → `Running`) in one transaction with the version update, so duplicate queue entries (import enqueue plus startup recovery) are harmless.
- **Final transaction:** passages, the `Storing/Completed` and `Completed/Completed` events, version `Indexed`, supersede of the previous active version, `ActiveVersionId` and run `Succeeded` commit together. `Document.UpdatedAt` is not touched.
- **Startup recovery retries:** if the run table cannot be read at start (database not ready, tables not migrated yet), the worker retries every `ProbeInterval` instead of failing the host. The integration factory starts the host before it migrates, which exposed this.
- **Embedding failures:** only transient errors (`HttpRequestException`, `IOException`, `SocketException`, `TimeoutException`, our own timeout) are retried. Anything else is an unexpected exception (`documents.ingestion_error`). A wrong vector count or length is never retried.
- **Deviation, overlap:** pieces of a code block or table that was split because it exceeds `MaxTokens` get no overlap prefix. They are self-contained (re-fenced, header rows repeated) and a prefix would put a stray fence in front of them. Overlap between ordinary passages of one section is as specified. A passage that results from a cross-section merge receives overlap only from a predecessor that ends in the section it starts in.
- **Merge details:** the minimum-size merge looks at adjacent passages only, and a merge that would leave no room for the overlap prefix the merged passage will receive is skipped.
- **Telemetry:** duration is recorded with an `outcome` tag (`completed`/`failed`); span names are `ingestion.run` and `ingestion.stage` (tag `stage`, including `storing`). ServiceDefaults now also subscribes to the `RagAssistant.*` activity sources. Tags carry ids only, never document text.
- **Packages:** `Microsoft.Extensions.AI.Abstractions` 10.11.0 (module), `CommunityToolkit.Aspire.Hosting.Ollama` and `CommunityToolkit.Aspire.OllamaSharp` 13.6.0 (AppHost and Host, ADR 0003).
- **Verified by hand (2026-10-14):** the AppHost was run with the `http` launch profile and `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`, on the pinned `ollama/ollama:0.40.4` image. Ollama started, the toolkit pulled `bge-m3` (1.2 GB) onto the `ragassistant-ollama-models` volume, and the Host did not wait for it. Signed in to the Web app as the dev `owner` user, a Markdown file was imported through the BFF (`POST /api/documents`, `202`) and `GET /api/documents/{id}/ingestion/events` was read with `EventSource` through the BFF: events 1 to 10 arrived in order (`queued` to `completed`, 143 characters, 1 passage, 1 batch) and the stream ended after `completed`. `GET /api/documents/{id}` then showed `status: active`, `passageCount: 1` and a `succeeded` latest run, and the database held `embedding_model = bge-m3`, `embedding_dimensions = 1024` and a stored vector of 1024 dimensions. The run took about 9 seconds from queued to finished, most of it the first call loading the model. This also confirms that the SSE stream passes through the BFF unbuffered; roadmap 1.6 still verifies it from the UI.
- **Local access note:** Keycloak is served over HTTPS at `https://localhost:8180` (the Web app redirects there). Calling it over plain `http` gets the connection closed, which is why `curl` and PowerShell token requests to `http://localhost:8180` failed. Use `https` (with `-k` or a trusted dev certificate) for the `ragassistant-dev` password grant.
- **Ollama image tag:** the developer pinned `OllamaImageTag = "0.40.4"` in `AppHost.cs` (like the Postgres and Keycloak tags) instead of the toolkit default.
- **Not done:** the streaming path through the BFF is verified when the UI consumes it (1.6), as planned. AC-30's "arch-check passes" was covered by the existing module-boundary unit test only.
- **Tests:** unit tests for the chunker, the embedder and option validation (`tests/Modules/RagAssistant.Modules.Documents.Tests`); integration tests in `tests/RagAssistant.Integration.Tests/Documents/Ingestion*` (AC-1 to AC-3, AC-9 to AC-27, AC-29). Each ingestion test starts its own application on its own database (`IngestionHarness`) with a scriptable `FakeEmbeddingGenerator`; the default factory registers an always-unavailable generator, so the spec 006 tests are unchanged. A second harness on the same database models a restart.
- **Clock in tests:** the retry backoff (AC-11) and the idle heartbeat (AC-24) run on a controllable `ManualTimeProvider` (`tests/RagAssistant.Integration.Tests/Fixtures`, also linked into the unit test project). It only moves when a test calls `Advance`, and timers (so `Task.Delay(…, TimeProvider)`) fire when it passes their due time. The embedder unit tests assert the 2 s then 4 s backoff exactly and a call timeout on the clock; the integration tests assert that at least 6 s (retries) and 30 s (two heartbeats at the 15 s default) of clock time passed while almost no wall time did. Other ingestion tests still use real millisecond intervals, which is fine because they assert outcomes, not schedules.
- **Test parallelism:** ingestion test classes use a parallel limit of four (`IngestionParallelLimit`); each application has its own connection pools and 35 at once exceeded Postgres's `max_connections`. Harness pools are small and short-lived for the same reason.
- **Test configuration:** harness documents use `Documents:Chunking:MinTokens = 1`, otherwise the minimum-size merge folds the tiny test documents into one passage.

- **Performance (measured by hand, 2026-10-14):** local Ollama on CPU in a Docker container, `bge-m3`, default options (400/50/80 tokens, batch size 16). Two generated Markdown files of about 47.5 KB each (10 chapters, 20 sections) were imported through the BFF and read through the stream. Each produced 40 passages in 3 embedding batches. First file: 16.7 s from the `Queued` event to `Completed` (chunking 12 ms, embedding 16.5 s, storing 6 ms); second file: 18.1 s (embedding 17.9 s). The worker picked the run up 75 to 118 ms after it was queued. This is well under the one-minute target, and embedding is over 99% of the time, so batch size and parallel batches are the only levers if it ever matters (see Deferred Decisions). The very first call after the model container starts is slower (about 9 s for a 143-character file, mostly loading the model); both measured runs came after that warm-up, so a truly cold start on a large file was not measured. The stream carried one event per stage transition with no visible delay; the 500 ms poll latency was not measured separately.

Changes made after the review of the first implementation (2026-10-14):

- **Probe:** any failure of the availability probe other than the caller's own cancellation means "not available". Provider clients report "model not pulled yet" in different ways, and an exception outside the transient list used to strand the run.
- **Crashed runs:** if a run throws out of the processor (in practice, the database failed while the run was being failed or claimed), the consumer waits `ProbeInterval`, resets that one run to `Queued` with a `Re-queued after an error` event and enqueues it again, retrying while the database is down. A restart is no longer needed.
- **Run spans are roots:** `ingestion.run` clears `Activity.Current`, so each run is its own trace instead of sharing the worker's startup trace.
- **Model name:** the name stored on a version is the one the provider reports (`EmbeddingGeneratorMetadata.DefaultModelId`); `Documents:Embeddings:Model` is only the fallback. The AppHost model and the config can no longer disagree silently in the stored data (the `Dimensions` check is unchanged).
- **Overlap:** an overlap tail that contains a code fence or table line is dropped (the passage simply starts without a prefix), so a passage never begins with unbalanced syntax.
- **Option ranges:** every `Documents:Ingestion` value must now be greater than 0, as specified. `EmbeddingMaxRetries = 0` and the arbitrary cap of 32 consumers are gone; tests that wanted "no retries" use 1.
- **Catch-alls:** the four filtered `catch (Exception)` sites carry a comment citing the rule they implement, and the `CLAUDE.md` anti-pattern now reads "without a filter or a stated reason".
- **Shared 404:** `DocumentErrors.NotFound` replaces three copies of the same error.
- **New tests:** probe and `ModelId` unit tests, a chunker test for the fence tail, the root-span assertion, provider-reported model, crash hand-back (a trigger that fails the first `Failed` event insert), shutdown mid-run writing nothing and finishing after a restart, and Host start failing on invalid options (AC-28).
