# ADR 0001: UI, AI, Streaming, and Testing Stack

> **Naming note (2026-10-15):** the backend project `RagAssistant.Host` has since been renamed `RagAssistant.Api` (Aspire resource `api`, configuration keys `services:api:*`, test stub `StubApi`). "The Host" in this document means that project.

**Status:** Accepted. Decision 5 amended by [ADR 0002](0002-identity-and-authentication.md)
**Date:** 2026-10-13
**Context documents:** [product.md](../product.md), [spec 001](../specs/001-aspire-postgres.md)

## Context

`product.md` describes a knowledge assistant whose core promise is trust: streamed answers with inspectable citations, explicit answer states (complete, partial, refused, error), and visible ingestion progress. Before writing the Documents and Chat specs, the following choices must be fixed:

- the target framework;
- the UI component suite;
- the AI abstraction layer (Microsoft.Extensions.AI alone vs. Microsoft Agent Framework);
- how the backend streams answers and progress to the UI;
- where the Blazor UI runs;
- the testing framework.

Candidates considered: .NET 11 with the experimental `Microsoft.AspNetCore.Components.AI` package, Microsoft Agent Framework (MAF) with the AG-UI protocol, Fluent UI Blazor, and Syncfusion Blazor.

## Decisions

### 1. Target framework: .NET 10 (LTS)

The whole solution stays on `net10.0`.

- .NET 10 is LTS (supported until November 2028); .NET 11 is STS.
- `Microsoft.AspNetCore.Components.AI` requires the .NET 11 SDK and is experimental for the whole .NET 11 cycle. It is **not used**. Revisit it when it ships stable.

### 2. UI component suite: Fluent UI Blazor only

`Microsoft.FluentUI.AspNetCore.Components` (MIT) is the only component library.

- It provides the generic primitives listed in `product.md` (layout, navigation, buttons, inputs, dialogs, badges, tabs, cards, tooltips, data grid, light/dark theming).
- The RAG domain components (`AnswerMessage`, `CitationList`, `CitationItem`, `SourcePreview`, `RefusalNotice`, `DocumentStatusBadge`, `PassageCard`, `RetrievalInspector`, …) are application-owned and built on Fluent primitives.
- Pick v4 or v5 when the UI project is created, based on whether v5 is stable at that point. Pin the version in `Directory.Packages.props`. **Decided in [spec 005](../specs/005-web-shell.md): v5.0.0** (stable at that point).

**Rejected: Syncfusion Blazor.** It is commercial, not open source. It is free only under the Community License, which has revenue, headcount and funding limits and is granted at Syncfusion's discretion. In a public demo repository, anyone cloning the project would need their own license key. Its distinctive components (PDF viewer, diagram, AI AssistView) are not needed for the first release. Mixing two design systems is also rejected.

### 3. AI abstractions: Microsoft.Extensions.AI only

Modules use `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>`. The concrete provider is wired only in the Host.

The answer flow is a fixed pipeline, implemented as ordinary handler code:

```text
rewrite follow-up → retrieve (pgvector + metadata filters) → evidence threshold
  → [refuse without LLM call] or generate (streamed) → validate citations → persist
```

| Need | Covered by |
|---|---|
| Streaming generation | `IChatClient.GetStreamingResponseAsync` |
| Embeddings | `IEmbeddingGenerator` |
| Query rewriting, structured output | `GetResponseAsync<T>` |
| Telemetry, logging, caching | `UseOpenTelemetry()`, `UseLogging()`, `UseDistributedCache()` middleware |
| Quality evaluation | `Microsoft.Extensions.AI.Evaluation` (separate harness, see §7) |

**Deferred: Microsoft Agent Framework and AG-UI.** MAF adds agent threads, tool-choosing agents, workflows, human-in-the-loop and AG-UI/A2A hosting, none of which the first release needs. Conversation history is owned by the Chat module's own tables. MAF is built on `IChatClient`, so adopting it later is additive. Reconsider it when one of these needs actually appears: multi-step questions, choosing between retrieval strategies, or model-invoked tools (`product.md`, challenge 10).

### 4. Streaming: native Server-Sent Events with a typed event contract

The Host streams with .NET 10 native SSE: `TypedResults.ServerSentEvents(IAsyncEnumerable<SseItem<T>>)` on the server, and `System.Net.ServerSentEvents.SseParser` on the client. Event payloads are DTO records. Exact names and fields are finalized in the Chat and Documents specs.

**Answer stream**: `POST /chat/conversations/{id}/messages` returns `text/event-stream`:

```text
event: question.accepted     {messageId}
event: retrieval.started
event: retrieval.completed   {passageCount, citations[]}
event: answer.delta          {text}                                        (repeated)
event: answer.completed      {status: complete|partial|refused, citedPassageIds[]}
event: error                 {code, retryable}
```

- Every event maps to a UI state required by `product.md`.
- A refusal can follow `retrieval.completed` directly, without an LLM call.
- Citations are sent before the answer text, so the UI can show sources while the answer streams.
- A client disconnect cancels generation through `HttpContext.RequestAborted`.

**Ingestion progress:**

- Upload returns `202 Accepted` with the document id. Ingestion never runs inside the HTTP request.
- A `BackgroundService` reading a `Channel<T>` processes documents and **persists each stage** to the Documents schema (queued → extracting → chunking → embedding n/m → indexed | failed).
- `GET /documents/{id}/ingestion/events` streams stage changes as SSE. Since state is persisted, page reloads and reconnections (`Last-Event-ID`) resume correctly.
- The persisted run table is the source of truth; the `Channel` only carries run ids to the worker. On startup, the worker re-queues every run still *queued* or in progress, so ingestion survives a restart.
- If the application ever runs as several instances, workers claim queued runs with PostgreSQL `FOR UPDATE SKIP LOCKED`.

**Rejected: Wolverine (or another messaging library).** The needs it would cover (durable background work, retries) are met by a persisted table and a `BackgroundService` using only EF Core and PostgreSQL. Modules communicate through interfaces, so no message bus is needed. A messaging framework would add concepts (convention-based handlers, generated code, its own storage) that cost more to explain than they bring to a demonstration project.

**Rejected: AG-UI.** It is an SSE protocol with a generic vocabulary (text, tool calls, state). Without MAF on the server and the .NET 11 Blazor AI components on the client, a domain-specific contract is simpler and carries citations and answer status natively.

### 5. UI hosting: Blazor Web App with WebAssembly interactivity

> **Amended by [ADR 0002](0002-identity-and-authentication.md):** the browser no longer calls the Host directly. `RagAssistant.Web` acts as a backend for frontend (BFF). It holds the session cookie and forwards `/api/**` (REST and SSE) to the Host through YARP with the user's access token. The other points below still apply.

A new `RagAssistant.Web` project (Blazor Web App) hosts the UI. Pages that stream (chat, ingestion status) use `InteractiveWebAssembly`, with prerendering for the initial load.

- The browser calls the Host's REST and SSE endpoints directly. This gives a real UI ↔ API boundary, and the API is testable without the UI.
- Blazor Server was rejected for these pages: its components run on the server, so consuming SSE would be a loopback call, and every UI update would travel over the SignalR circuit.
- Streaming requests from WebAssembly must call `request.SetBrowserResponseStreamingEnabled(true)`; otherwise the browser buffers the whole response.
- The AppHost orchestrates `RagAssistant.Web` alongside the Host, with a service reference to it.

### 6. Testing: TUnit on Microsoft.Testing.Platform

**TUnit** is the only testing framework, running on Microsoft.Testing.Platform (configured in `global.json`). xUnit, NUnit and MSTest are not used.

- Test projects are `OutputType=Exe`. They use `[Test]`, `[Arguments(...)]` and awaited assertions (`await Assert.That(x)...`).
- **Integration tests:** `WebApplicationFactory<Program>` plus Testcontainers PostgreSQL (`pgvector/pgvector:0.8.7-pg18-trixie`), shared per test session through `[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]`.
- **SSE endpoints** are tested end to end: the test `HttpClient` reads the response stream with `SseParser` and asserts the event sequence (e.g. `retrieval.completed` → `answer.completed{status: refused}` when evidence is insufficient).
- **AI calls:** `IChatClient` and `IEmbeddingGenerator` are replaced with deterministic fakes. Tests never call a real model.
- **Blazor components:** tested with bUnit, run under TUnit. Confirm bUnit + TUnit compatibility when the first component test is written; if they are incompatible, record the alternative in a follow-up ADR.

### 7. Evaluation runs outside `dotnet test`

Answer-quality evaluation needs real model calls, which the test suite forbids. It lives in a separate harness: a console project using `Microsoft.Extensions.AI.Evaluation` against a versioned golden dataset, run on demand. Retrieval metrics (whether the expected passages were retrieved) need no chat model, but they do need the real embedding model, so they run in the same harness. The test suite still covers retrieval **mechanics** (ranking, filters, thresholds) deterministically, using fake embeddings with controlled vectors.

## Consequences

**Positive**
- Every dependency is stable and open source, with no license keys. Anyone can clone and run the repository.
- One AI abstraction layer (Microsoft.Extensions.AI) and a pipeline you can read top to bottom and test deterministically.
- The streaming contract is part of the product: answer states, citations and progress are explicit in the API and covered by integration tests.
- MAF, AG-UI and the Blazor AI components remain open as later additions; nothing chosen here blocks them.

**Negative**
- The chat UI (streaming rendering, Markdown, auto-scroll, retry) is hand-built instead of coming from a ready-made component.
- The SSE event contract is custom and must be versioned by us.
- A second runnable project (`RagAssistant.Web`) and a WebAssembly download on first visit.
- A run interrupted by a restart starts again from its first stage, not from where it stopped. This is acceptable for the document sizes in scope.

## Revisit when

- `Microsoft.AspNetCore.Components.AI` ships stable on an LTS release.
- A feature needs model-invoked tools, multi-step reasoning, or human approval of actions (→ MAF).
- A second, non-Blazor client needs agent interactions (→ reconsider AG-UI).
