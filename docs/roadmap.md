# RagAssistant — Implementation Roadmap

**Date:** 2026-10-13
**Inputs:** [product.md](product.md), [ADR 0001](adr/0001-ui-ai-streaming-stack.md), [ADR 0002](adr/0002-identity-and-authentication.md), [spec 001](specs/001-aspire-postgres.md)

This document decides **what to build, in which order, and with which tools**. Each step is a vertical slice delivered by one or a few pull requests, each with its own spec. The order follows three rules:

1. **Dependencies first.** Nothing is built on a contract that does not exist yet: identity before endpoints, documents before retrieval, retrieval before answers.
2. **Show something working early.** A thin end-to-end path (import a document, ask, get a cited answer or a refusal) comes before polishing any single part.
3. **Measure before tuning.** Evaluation exists before hybrid search, reranking, or threshold tuning (`product.md`, challenges 2 and 8).

## Where we are

| Done | Not yet |
|---|---|
| Aspire AppHost, PostgreSQL + pgvector, pgweb, MigrationService, `DatabaseMigrator`, TUnit integration fixture (spec 001) | Shared kernel (Result, Mediator, validation, ProblemDetails, endpoint discovery), modules, Keycloak, Web project, CI, project skills |

The Host still has a placeholder `MapGet("/")` in `Program.cs`. It is removed in step 0.3.

---

## Phase overview

```text
Phase 0  Foundations ──────────────────────────────────────────────┐
  0.1 Decisions   0.2 Tooling   0.3 Shared kernel   0.4 CI         │
  0.5 Identity    0.6 Web shell                                    │
                                                                   ▼
Phase 1  Documents  ── import → background ingestion → progress SSE → browse passages
                                                                   ▼
Phase 2  Retrieval  ── IDocumentSearch contract, vector + metadata filters, evidence threshold
                                                                   ▼
Phase 3  Chat       ── refusal path → streamed grounded answer → citations UI → follow-ups
                                                                   ▼
Phase 4  Diagnose   ── request traces, admin area           ┐
Phase 5  Evaluate   ── golden dataset, metrics harness       ┘ (in parallel)
                                                                   ▼
Phase 6  First release hardening ── i18n, accessibility, home, README, versioning UX
                                                                   ▼
Later    hybrid search · reranking · conflict surfacing · Knowledge module · DOCX/PDF
```

**First-release cut** (`product.md`, "First release definition"): end of Phase 6. Phases 4 and 5 are part of it ("basic evaluation", "basic observability", "administrative visibility").

---

## Phase 0 — Foundations

### 0.1 Close the open decisions — ✅ Done

Decided 2026-10-13: D1 → [ADR 0003](adr/0003-model-provider.md); D2, D3, D4 → [ADR 0004](adr/0004-document-versioning.md).

These block specs and must be answered first (`product.md`, "Open questions"):

| # | Decision | Blocks | Notes |
|---|---|---|---|
| D1 | **Model provider** (chat + embeddings) | 1.3, 3.x | The embedding model fixes `vector(n)` and must be multilingual (FR/EN). For "clone and run" without API keys, consider a **local model container in the AppHost** (e.g. Ollama through the Aspire Community Toolkit), with a hosted provider as an option. Also consider a deterministic **demo mode** (fake `IChatClient`) so the UI runs with no model at all. |
| D2 | **Document versioning model** | 1.1 | Versioned documents + stable passage IDs + snapshot of cited passages per answer (challenge 5). Expensive to change later. |
| D3 | Targets: corpus size, time to first token, ingestion time | 1.3, 2.x | Rough orders of magnitude are enough. |
| D4 | Retention of conversations and traces | 3.2, 4.1 | Can be "keep everything" for a demo, but write it down. |

**Kit:** `/spec` can be used to interrogate each decision. Record D1 and D2 as ADRs (`docs/adr/0003-…`, `0004-…`).

### 0.2 Tooling: skills and agent setup — ✅ Done

`CWM.RoslynNavigator` 0.8.0 installed globally; CLAUDE.md extended to forbid MSTest guidance; `dotnet-blazor`, `dotnet-test` and `dotnet-ai` plugins installed from `dotnet/skills`.

See [Skills and tooling](#skills-and-tooling) below. Install the extra plugins **before** writing Blazor or test code.

### 0.3 Shared kernel and API conventions — ✅ Done

Small, but everything depends on it:

- `Result` / `Result<T>` and mapping to `TypedResults` + ProblemDetails (RFC 9457);
- Mediator (source-generated) + validation pipeline behavior (FluentValidation);
- `IEndpointGroup` + `app.MapEndpoints()` auto-discovery; remove the placeholder endpoint;
- global exception handler, OpenAPI + Scalar (Development); built-in `ILogger` + OpenTelemetry, no Serilog (ADR 0005);
- `TimeProvider` registration.

**Kit:** `/plan` → implement → `/verify`. Skills: `error-handling`, `minimal-api`, `openapi`, `scalar`, `vertical-slice`.
**Done when:** a sample endpoint group returns ProblemDetails on validation failure, covered by an integration test.

### 0.4 Continuous integration — ✅ Done

[Spec 003](specs/003-continuous-integration.md): `.github/workflows/ci.yml` runs restore, Release build, format check and tests on every PR and push to `main`.

GitHub Actions: restore, build (warnings as errors), `dotnet format --verify-no-changes`, and `dotnet test` with Docker (Testcontainers runs on `ubuntu-latest`).

**Why now:** every following PR is validated automatically (`product.md`, "Delivery workflow").
**Kit:** `ci-cd` skill. **Extra skill:** `run-tests` (dotnet/skills) for the exact Microsoft.Testing.Platform/TUnit command line.

### 0.5 Identity (ADR 0002)

- Keycloak in the AppHost with the committed realm file (clients, three roles, test users, flat `roles` claim, audience mapper).
- Host: JWT bearer, policies `KnowledgeSeeker` / `ContentOwner` / `Maintainer`, policy name constants in Shared.
- Test infrastructure: test signing key in `RagAssistantAppFactory`, `CreateClientAs(Persona…)` helper, and the Keycloak realm smoke test (Testcontainers).

**Why before any business endpoint:** every endpoint requires authentication. Adding it later means rewriting every test.
**Kit:** `/spec` → `/plan` → `/tdd`. Skills: `authentication`, `aspire`, `testing`, `security-scan` at the end.
**Done when:** a protected sample endpoint returns 401 / 403 / 200 per persona in tests, and sign-in works against Keycloak from the AppHost.

### 0.6 Web shell (ADR 0001 + 0002)

- `RagAssistant.Web`: Blazor Web App, `InteractiveWebAssembly`, Fluent UI, wired into the AppHost.
- BFF: OIDC sign-in/sign-out, cookie session, YARP `/api/**` → Host with token forwarding, no response buffering, antiforgery, auth state serialization to WebAssembly.
- App layout: header, navigation (`/`, `/chat`, `/documents`, `/knowledge` hidden, `/admin` visible to maintainers only), light/dark theme, `LanguageSwitcher`.
- **Localization FR/EN from the first page:** resource files and culture switching. Retrofitting them is costly.
- Shared components: `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `ConfirmDialog`.

**Kit:** `/spec` → `/plan`. **Extra skills:** `create-blazor-project`, `configure-auth`, `plan-ui-change`, `author-component`, `support-prerendering`, `fetch-and-send-data`. Kit skills: `aspire`, `authentication`.
**Done when:** clone → run AppHost → sign in as each test user → see the role-appropriate navigation in both languages and both themes.

---

## Phase 1 — Documents module

The module owns Corpus Management + Ingestion (+ Retrieval in Phase 2). Start with **one spec for the data model and import**, then one spec per later slice.

| Step | Slice | Key points | Policy |
|---|---|---|---|
| 1.1 | **Spec + data model** | `Document` (stable identity, metadata: title, language, owner, tags, effective date, status), `DocumentVersion`, `Passage` (stable ID, position, section path, parent section), `IngestionRun` with stages. `vector(n)` from D1, HNSW index, `HasPostgresExtension("vector")`, `documents` schema. | — |
| 1.2 | **Import document** | Markdown/plain text upload + metadata → `202 Accepted` with document ID; version created with status *queued*. Validation of format, size, required metadata. | ContentOwner |
| 1.3 | **Background ingestion** | `BackgroundService` + `Channel<T>`: extract → normalize → structure-aware chunking (Options: max size, overlap) → embed (batched) → store. Persist every stage; failures are visible and retryable; a document is retrievable only when *active* and *indexed*. The persisted run table is the source of truth; on startup, re-queue runs left queued or in progress. No messaging library. | — |
| 1.4 | **Ingestion progress (SSE)** | `GET /documents/{id}/ingestion/events`: persisted stage changes, `Last-Event-ID` resume. Integration test reads the stream with `SseParser`. | ContentOwner |
| 1.5 | **Browse corpus** | List with filters (status, language, owner, tags, date) and paging; document detail; passages of a version. | KnowledgeSeeker |
| 1.6 | **Documents UI** | `DocumentTable`, `DocumentUpload`, `DocumentStatusBadge`, live `IngestionStatus`, document detail with `DocumentMetadataPanel` and `PassageList`/`PassageCard`. | — |
| 1.7 | **Lifecycle** | Edit metadata, upload a new version (history preserved), archive, reprocess. | ContentOwner |

**Kit:** `/scaffold` (module), `/spec`, `/plan`, `/tdd`, `/migrate` (each EF migration), `/verify`. Skills: `ef-core`, `vertical-slice`, `configuration`, `logging`, `opentelemetry`. **Extra:** `collect-user-input` (upload, filters), `fetch-and-send-data`, `coordinate-components`.
**Demo after Phase 1:** a content owner imports a Markdown policy, watches it being indexed live, and browses its passages.

## Phase 2 — Retrieval

| Step | Slice | Key points |
|---|---|---|
| 2.1 | **Search contract** | `IDocumentSearch.SearchAsync(query, filters, topK, ct)` in `Shared/Contracts/Documents`, returning `RetrievedChunk` records (passage ID, version, document title, section, excerpt, score, document status). Internal implementation in Documents. |
| 2.2 | **Vector search + metadata filters** | Query embedding (cached with HybridCache), `CosineDistance` + `OrderBy` + `Take`, *active + indexed only* unless history is requested, language/tag filters. |
| 2.3 | **Evidence rules** | Configurable `topK`, minimum score, near-duplicate reduction; result distinguishes *no result* from *low confidence*. |
| 2.4 | **Search endpoint (diagnostic)** | `GET /documents/search?q=` for maintainers: shows ranked passages and scores, and is reused later by the retrieval inspector. |

**Kit:** `/spec`, `/tdd`, `caching`, `ef-core`. **Extra:** `optimizing-ef-core-queries` once real volumes exist.
**Tests:** a fake embedding generator with **controlled vectors** makes ranking and filter tests deterministic.

## Phase 3 — Chat module

Order inside the phase matters: **the refusal path first.** It needs no LLM, proves the pipeline and the SSE contract, and embodies the product's first principle.

| Step | Slice | Key points | Policy |
|---|---|---|---|
| 3.1 | **Spec + conversations** | `Conversation` (owner = `sub`), `Message`, answer status, cited passage snapshots, `chat` schema. Create, list own, get own. | KnowledgeSeeker |
| 3.2 | **Ask → refusal (SSE)** | `POST /chat/conversations/{id}/messages` streams `question.accepted` → `retrieval.started` → `retrieval.completed` → `answer.completed{status: refused}` when evidence is insufficient. No model call. | KnowledgeSeeker |
| 3.3 | **Grounded streamed answer** | Prompt built in one place with passage identifiers; `answer.delta` streaming; citation validation (only retrieved passages may be cited); status *complete* / *partial*; persist answer + snapshot; cancellation on disconnect; `error` event with `retryable`. | KnowledgeSeeker |
| 3.4 | **Chat UI** | `QuestionInput`, `ConversationThread`, streaming `AnswerMessage`, `CitationList`/`CitationItem`, `SourcePreview` (panel, conversation stays visible), `RefusalNotice`, error + retry, cancel. All states from `product.md` "Assistant — /chat". | — |
| 3.5 | **Follow-up questions** | Bounded history window + rewriting into a standalone retrieval query (`GetResponseAsync<T>`); conversation never used as evidence. | — |
| 3.6 | **Feedback** | Helpful / not helpful / report issue, stored with the message. | KnowledgeSeeker |

**Kit:** `/spec`, `/plan`, `/tdd`, `resilience` (provider calls), `error-handling`. **Extra:** `technology-selection` (dotnet-ai) when wiring the provider from D1; `author-component`, `coordinate-components`, `use-js-interop` (auto-scroll, copy reference) for the UI.
**Tests:** fake `IChatClient` that streams scripted tokens and citation markers. Assert the **event sequence** and the persisted snapshot.
**Demo after Phase 3 = the core promise:** ask, watch the answer stream, open a citation, get an honest refusal for an off-corpus question.

## Phase 4 — Diagnose (Observability + Admin)

| Step | Slice | Key points |
|---|---|---|
| 4.1 | **Request traces** | One record per question: rewritten query, retrieved passages + scores, threshold decision, status, citations, timings, model/token usage, trace ID. No full document text. Retention per D4. *Start writing these records in 3.3; this step adds reading them.* |
| 4.2 | **Admin area** (`Maintainer`) | Recent requests → `RetrievalInspector`; ingestion failures with retry; corpus health (counts per status, failed runs, stale documents); link to the Aspire trace. |
| 4.3 | **Metrics** | OpenTelemetry metrics: ingestion durations, retrieval result counts, refusals, time to first token, errors per stage. |

**Kit:** `opentelemetry`, `logging`, `serilog`. **Extra:** `plan-ui-change` (dashboard decomposition).

## Phase 5 — Evaluate

| Step | Slice | Key points |
|---|---|---|
| 5.1 | **Golden dataset** | Versioned file: questions (FR + EN), expected passages, expected behavior (answer / partial / refusal), on a small committed sample corpus that is also used for demos. |
| 5.2 | **Evaluation harness** | Console project, run on demand (never in `dotnet test`): retrieval metrics (recall@k, MRR against expected passages) and answer checks with `Microsoft.Extensions.AI.Evaluation` (groundedness, relevance, refusal correctness). Reproducible report per run, keyed by configuration and corpus version. |
| 5.3 | **Results in admin** | Latest runs and their comparison, visible to maintainers. |

**Why here:** the first real numbers set the defaults for `topK`, thresholds and chunk size, and gate every later retrieval change.

## Phase 6 — First-release hardening

- Home page (`/`): value statement, ask CTA, suggested questions, corpus summary.
- Bilingual completion: every page and state in FR and EN; answers in the user's language; cross-language retrieval check in the golden dataset.
- Accessibility pass: keyboard paths through Journeys 1–3, focus, contrast, screen-reader labels for citations.
- Version UX: superseded/archived badges on old citations; "this source has changed" in `SourcePreview`.
- README: clone-and-run instructions, test users, sample corpus, model setup (D1).
- Review passes: `/code-review`, `/security-scan`, `/health-check`, `/de-sloppify`, `arch-check` (modules never reference each other).

---

## Later (each needs evidence from Phase 5 or a real need)

| Item | Trigger |
|---|---|
| Hybrid search (Postgres full-text FR/EN + vector) | Evaluation shows term-sensitive misses |
| Reranking | Evaluation shows good recall but poor ordering |
| Conflict surfacing between sources | Golden-dataset cases with conflicting documents |
| Knowledge module (entities, relationships, `/knowledge`) | Questions that need relationships, after the first release |
| DOCX / simple PDF import | Content owners need it |
| Per-corpus access rules | A second audience with restricted content (ADR 0002) |

---

## Per-step workflow with dotnet-claude-kit

Each step above follows the same loop (`product.md`, "Delivery workflow"):

```text
/spec  →  docs/specs/NNN-<slug>.md (acceptance criteria, out of scope)
/plan  →  implementation plan, files, risks (plan mode)
/scaffold  (new module or feature skeleton, when relevant)
/tdd   →  TUnit tests first: integration (WebApplicationFactory + Testcontainers) and unit
/migrate   (any schema change)
/verify    →  build, format, tests
/code-review  →  then PR, CI green, human approval
```

Useful between steps: `/build-fix` when the build breaks, `/de-sloppify` before a PR, `arch-check` after adding a module or a contract, `instinct-system` to capture corrections.

---

## Skills and tooling

### Kit coverage and gaps

dotnet-claude-kit covers the backend well: Aspire, EF Core, minimal APIs, OpenAPI, errors, caching, configuration, DI, logging, OpenTelemetry, resilience, authentication, CI/CD, and the spec/plan/TDD/verify workflow. It has **no Blazor skill and no AI/LLM skill**, and its `testing` skill is written for xUnit.

### Plugins to add from [dotnet/skills](https://github.com/dotnet/skills)

| Plugin | Skills used here | Needed from | Why |
|---|---|---|---|
| `dotnet-blazor` | `create-blazor-project`, `configure-auth`, `author-component`, `plan-ui-change`, `collect-user-input`, `fetch-and-send-data`, `coordinate-components`, `support-prerendering`, `use-js-interop` | 0.6 | The kit has nothing for Blazor. `configure-auth` handles auth per render mode; `support-prerendering` prevents duplicate loads and flicker with WebAssembly + prerendering. |
| `dotnet-test` | `run-tests`, `filter-syntax`, `platform-detection`, `test-anti-patterns`, `assertion-quality`, `test-gap-analysis` | 0.4 | Knows Microsoft.Testing.Platform and **TUnit** filter syntax; audits test quality. |
| `dotnet-ai` | `technology-selection` | 0.1 (D1), 3.3 | Guidance on Microsoft.Extensions.AI and local inference when choosing and wiring the provider. |
| `dotnet-data` *(optional)* | `optimizing-ef-core-queries` | Phase 2+ | Reviews query and bulk-write performance (embedding storage, search). |

Not needed: `dotnet-aspnetcore` (overlaps the kit's minimal-api/openapi), `dotnet-msbuild`, `dotnet-nuget` (central package management is already in place), `dotnet-diag` (until a performance problem appears), `dotnet11` (we stay on .NET 10).

Install from an interactive `claude` terminal:

```bash
/plugin marketplace add dotnet/skills
/plugin install dotnet-blazor@dotnet-agent-skills
/plugin install dotnet-test@dotnet-agent-skills
/plugin install dotnet-ai@dotnet-agent-skills
```

**Guard against conflicting guidance.** `dotnet-test` also ships MSTest-specific skills (`writing-mstest-tests`, `code-testing`), and the kit's `testing` skill uses xUnit. `CLAUDE.md` already forbids xUnit/NUnit attributes; extend that rule to **MSTest** when installing the plugin, so TUnit stays the only test framework (ADR 0001).

### Project skills to create (with `skill-creator`)

No public skill covers these project-specific conventions. Create them once the first example exists, so they capture real code:

| Skill | Create after | Captures |
|---|---|---|
| `fluentui-blazor-conventions` | 0.6 | Fluent UI version and components in use, theming tokens, layout patterns, how domain components wrap Fluent primitives |
| `sse-endpoints` | 1.4 | Event naming, DTO records, `TypedResults.ServerSentEvents`, BFF passthrough, `SseParser` tests, `SetBrowserResponseStreamingEnabled` on the client |
| `rag-pipeline` | 3.3 | Prompt construction, citation format and validation, evidence threshold, fake `IChatClient` / embedding patterns for tests |

Store them in `.claude/skills/` in the repository so they are versioned with the code.

### Environment note

The `cwm-roslyn-navigator` MCP server (used by `arch-check`, `/code-review` and the module-boundary checks) failed to connect in the session that produced this roadmap. Make sure `dotnet tool install -g CWM.RoslynNavigator` is installed and the server starts before Phase 1.
