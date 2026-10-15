# RagAssistant — Modular Monolith

## Project Context

RagAssistant is a .NET 10 demo of a Retrieval-Augmented Generation (RAG) assistant: users ingest documents, the system chunks and embeds them into PostgreSQL + pgvector, and a chat endpoint answers questions grounded in the retrieved chunks.

It is a **modular monolith**: one backend deployable (the API) composed of independent modules with strict boundaries. Each module owns its features, data (own DbContext + schema), and logic. Modules use **Vertical Slice Architecture** internally (moderate domain complexity, solo developer — keep ceremony low). The UI is a separate Blazor project (`RagAssistant.Web`) acting as a backend for frontend (BFF): it holds the session cookie and forwards `/api/**` (REST + SSE) to the API through YARP with the user's access token.

Stack decisions are recorded in [ADR 0001](docs/adr/0001-ui-ai-streaming-stack.md) (framework, UI suite, AI layer, streaming, testing) [ADR 0002](docs/adr/0002-identity-and-authentication.md) (identity and authentication), [ADR 0003](docs/adr/0003-model-provider.md) (model provider) and [ADR 0004](docs/adr/0004-document-versioning.md) (document versioning, citation snapshots, targets, retention) and [ADR 0005](docs/adr/0005-api-kernel-libraries.md) (API kernel libraries, Mediator topology, no Serilog) and [ADR 0006](docs/adr/0006-pdf-import.md) (PDF import: PdfPig, original-file store, targets) and [ADR 0007](docs/adr/0007-browser-smoke-tests-playwright.md) (Playwright browser smoke tests).

### Modules

| Module | Owns | Schema |
|--------|------|--------|
| `Documents` | Upload/ingest documents, text extraction, chunking, embedding generation, vector storage, similarity search. Exists: data model, `POST /api/documents`, `GET /api/documents/{id}` ([spec 006](docs/specs/006-documents-data-model-and-import.md)); background ingestion (`IngestionWorker`, chunker, batched embedding), `POST /api/documents/{id}/ingestion/retry` and the `GET …/ingestion/events` SSE stream ([spec 007](docs/specs/007-background-ingestion-and-progress.md)); listing with filters and paging, facets, versions and passages of a version (`GET /api/documents`, `/facets`, `/{id}/versions`, `/{id}/versions/{n}/passages`) and the Documents UI ([spec 008](docs/specs/008-browse-corpus-and-documents-ui.md)); edit metadata (`PUT`, `If-Match`/`ETag`), new version, reprocess, archive and restore, with the matching detail-page actions ([spec 009](docs/specs/009-document-lifecycle.md)); PDF import up to 100 MiB (streamed upload into a content-addressed file store, text-layer extraction with page numbers on passages, scanned PDFs rejected at upload), `GET …/versions/{n}/file` for the original and `progress` events inside long ingestion stages ([spec 010](docs/specs/010-pdf-import.md)); search is still to come | `documents` |
| `Chat` | Conversations and messages, prompt building, retrieval orchestration, LLM answer generation with citations. Not started | `chat` |

Modules are added with `/scaffold`. In each module's `AddMediator(...)` set `GenerateTypesAsInternal = true` and `Assemblies = [typeof(<Module>Module)]`: the default scans every referenced assembly and generates public helper types, which collide with other modules and with the test projects' own Mediator (ADR 0005). A module reads its connection string itself (`UseNpgsql(connectionString, o => o.UseVector())`) and ships an `IDesignTimeDbContextFactory` so `dotnet ef` does not need to start the API.

## Tech Stack

- **.NET 10** (LTS) / C# 14 — do not move to .NET 11 (STS)
- **.NET Aspire 13.6.1** — dev-only orchestration (AppHost, ServiceDefaults, dashboard); see [spec 001](docs/specs/001-aspire-postgres.md)
- **ASP.NET Core Minimal APIs** — `IEndpointGroup` per feature with `app.MapEndpoints()` auto-discovery
- **Entity Framework Core** + **Npgsql** — one DbContext per module, PostgreSQL
- **pgvector** (`Pgvector.EntityFrameworkCore`) — embedding storage and similarity search (`vector` columns, HNSW index, cosine distance)
- **Microsoft.Extensions.AI** — `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` abstractions; the concrete provider is wired only in the API: local Ollama in the AppHost by default (multilingual embeddings, `bge-m3` / 1024 dims initially) plus a deterministic fake-chat demo mode (ADR 0003). The answer flow is a fixed pipeline in handler code — **Microsoft Agent Framework and AG-UI are deferred** (ADR 0001)
- **Server-Sent Events** — native .NET 10 `TypedResults.ServerSentEvents` with typed event DTOs for answer streaming and ingestion progress; clients parse with `SseParser`
- **Blazor Web App** (`RagAssistant.Web` server/BFF + `RagAssistant.Web.Client` WebAssembly; layout, pages and shared components live in the client) — `InteractiveWebAssembly`, prerendering on. English only: plain English text, no localization ([spec 005](docs/specs/005-web-shell.md)). The BFF forwards `/api/**` to the API through YARP as a pass-through (no path rewriting), attaches the session's access token, requires `X-Requested-With: ragassistant` on POST/PUT/PATCH/DELETE (CSRF), and returns ProblemDetails for 401/403/502/504. No token refresh: an expired session re-challenges and the Keycloak SSO session signs the user back in
- **Fluent UI Blazor** (`Microsoft.FluentUI.AspNetCore.Components`) — the only component suite; RAG domain components are app-owned. No Syncfusion or second design system
- **Background ingestion** — `BackgroundService` + `Channel<T>`, stage state persisted in the `documents` schema
- **PdfPig** (NuGet id `PdfPig`, namespace `UglyToad.PdfPig`) — reads the text layer, layout and outline of PDFs; **no OCR** (ADR 0006). It is used only inside the Documents module, behind `IPdfTextExtractor` (ingestion) and `IPdfProbe` (upload check). The browser sends files itself through `wwwroot/js/upload.js` (`XMLHttpRequest`, behind `IFileUploader`), so a 100 MiB PDF never enters the WebAssembly heap
- **HybridCache** — caching (e.g. query embeddings, retrieval results); no L2 backend yet
- **Mediator** (source-generated, MIT) — intra-module command/query dispatch; one scoped Mediator per module with `ValidationBehavior`, inject the module's concrete generated `Mediator` (aliased, e.g. `ModuleMediator`), never `IMediator`/`ISender` (ADR 0005)
- **FluentValidation** — request validation
- **Logging** — built-in `ILogger` + OpenTelemetry (logs, traces, metrics) via ServiceDefaults; no Serilog (ADR 0005)
- **TUnit** + **WebApplicationFactory** + **Testcontainers (PostgreSQL)** — testing, on Microsoft.Testing.Platform; **bUnit** for Blazor components
- **Microsoft.Extensions.AI.Evaluation** — answer-quality evaluation in a separate harness, never in `dotnet test`
- **Playwright for .NET** (`Microsoft.Playwright`) — real-browser smoke tests in `tests/RagAssistant.Ui.Smoke`, outside the solution and `dotnet test --solution`; for what bUnit cannot show (real file input, dialogs, focus, keyboard, multi-tab). Load the `ui-smoke-tests` skill to run, write or debug them ([ADR 0007](docs/adr/0007-browser-smoke-tests-playwright.md))
- **Auth** — Keycloak (dev-only container, `Aspire.Hosting.Keycloak`, preview) configured entirely by the committed realm file `src/RagAssistant.AppHost/Realms/ragassistant-realm.json` (no data volume — re-imported every run). API: JWT bearer (`AddKeycloakJwtBearer`, audience `ragassistant-api`, `RoleClaimType = "roles"`). Web: OIDC code + PKCE, cookie session, tokens never sent to the browser. Roles `knowledge-seeker` ⊂ `content-owner` ⊂ `maintainer`; policies `KnowledgeSeeker`, `ContentOwner`, `Maintainer` (names in `RagAssistant.Shared/Common/Security`); a fallback policy requires an authenticated user on every endpoint, so opt out explicitly with `AllowAnonymous`. Realm clients: `ragassistant-web` (BFF), `ragassistant-api` (audience), `ragassistant-dev` (public, password grant, dev tokens and the smoke test). Claims are not remapped (`sub`, `roles` as issued)
- **Messaging** — none; no message bus or messaging library (see Module Communication)

Package versions are managed centrally in `Directory.Packages.props` — never put a `Version` on a `PackageReference`.

**Minimal dependencies.** This is a demonstration project: every library must earn its place. Prefer what .NET 10, ASP.NET Core, EF Core and PostgreSQL already provide. Adding a new library or infrastructure component requires an ADR in `docs/adr/`.

## Architecture

```
RagAssistant.slnx
Directory.Build.props                    # net10.0, nullable, warnings-as-errors, analyzers
Directory.Packages.props                 # Central package management
global.json                              # SDK pin + Microsoft.Testing.Platform runner

src/
  RagAssistant.AppHost/                  # Aspire dev entry point: postgres (pgvector), pgweb, keycloak, migrations, host, web
    Realms/ragassistant-realm.json       # Dev-only clients, roles, test users — the source of truth for identity
  RagAssistant.ServiceDefaults/          # AddServiceDefaults(): OpenTelemetry, health checks, resilience
  RagAssistant.MigrationService/         # Worker: applies all module migrations, then exits
  RagAssistant.Api/                      # HTTP API — wires modules, AI providers, middleware
    Program.cs
    appsettings.json
  RagAssistant.Web/                      # Blazor Web App server + BFF: OIDC sign-in, cookie session, YARP to the API
  RagAssistant.Web.Client/               # WebAssembly client: layout, pages, shared components (Fluent UI Blazor 5)

  RagAssistant.Shared/                   # Shared kernel (thin!)
    Contracts/                           # Public cross-module contracts (interfaces + DTO records)
    Persistence/                         # DatabaseMigrator + AddMigrationTarget<TContext>()
    Common/
      Result.cs                          # Result pattern
      Behaviors/                         # Mediator pipeline behaviors
      Extensions/

  Modules/
    [Module]/
      RagAssistant.Modules.[Module]/
        Features/
          [Feature]/
            [Operation].cs               # Command/Query + Handler + Response + Validator
        Persistence/
          [Module]DbContext.cs
          Configurations/
          Migrations/
        [Module]Module.cs                # AddXxxModule(...) + AddXxxPersistence(...)

tests/
  RagAssistant.Integration.Tests/        # Full-app tests (WebApplicationFactory + Testcontainers)
  RagAssistant.Web.Tests/                # bUnit component tests + BFF tests against an in-process stub API (no Docker)
  RagAssistant.Ui.Smoke/                 # Playwright browser smoke tests against a running AppHost (not in the .slnx, run on demand)
  Modules/
    RagAssistant.Modules.[Module].Tests/
```

### Feature File Convention

```csharp
public static class IngestDocument
{
    public record Command(string FileName, string Content) : IRequest<Result<Response>>;
    public record Response(Guid DocumentId, int ChunkCount);
    public class Validator : AbstractValidator<Command> { }
    internal sealed class Handler(
        DocumentsDbContext db,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        TimeProvider clock) : IRequestHandler<Command, Result<Response>> { }
}
```

### Module Communication

No message bus. Modules communicate synchronously through contracts:

- A module exposes capabilities to other modules **only** through interfaces + DTO records in `RagAssistant.Shared/Contracts/[Module]/` (e.g. `IDocumentSearch.SearchAsync(string query, int topK, CancellationToken ct)` returning `RetrievedChunk` records). The owning module implements it `internal`ly and registers it in `AddXxxModule`.
- Never reference another module's project, DbContext, entities, or handlers.
- Background work (ingestion) uses a `BackgroundService` + `Channel<T>`, with the work state persisted in the owning module's tables: the persisted run table is the source of truth, and unfinished runs are re-queued on startup. If multiple instances ever run, workers claim rows with `FOR UPDATE SKIP LOCKED`. No messaging library (ADR 0001).

### Database Isolation

One PostgreSQL database (`ragassistant`, connection string name `ragassistant`), one schema per module (`HasDefaultSchema("documents")`), one migrations history per module. Enable the extension in the Documents module: `modelBuilder.HasPostgresExtension("vector")`. Configure Npgsql with `o.UseVector()`.

### Migrations & Aspire

- Never call `Database.MigrateAsync()` from the API. Each module exposes a public `AddXxxPersistence(IServiceCollection, IConfiguration)` that registers its DbContext and calls `services.AddMigrationTarget<XxxDbContext>()`; `AddXxxModule` calls it too.
- Register every module's `AddXxxPersistence` in `src/RagAssistant.MigrationService/Program.cs`. The AppHost runs the MigrationService before the API (`WaitForCompletion`); a failed migration keeps the API from starting.
- The integration-test fixture runs the same `DatabaseMigrator`, so register persistence in the API via `AddXxxModule` and tests pick it up.
- Dev Postgres: image `pgvector/pgvector:0.8.7-pg18-trixie`, `localhost:5433`, persistent container + volume `ragassistant-postgres-data` mounted at `/var/lib/postgresql` (PG18 layout — don't switch to `WithDataVolume`). The password is generated by Aspire into the AppHost user secrets (`Parameters:postgres-password`).
- Original files of PDF versions live outside Postgres in the file store (`IOriginalFileStore`, `Documents:Storage:Path`): content-addressed (`pdf/<sha256>.pdf`), written through `tmp/` and moved atomically, never deleted. The AppHost passes `<repo>/.data/documents` (git-ignored) as `Documents__Storage__Path`; tests use a temporary folder per factory. Back it up together with the database.
- The AppHost enables the Aspire CLI bundle (`AspireUseCliBundle`): `aspire start`, `ps`, `logs`, `describe` and `stop` work against it, and `dotnet run` still works. Keep the CLI at the SDK's version (13.6.1); an older CLI cannot connect.

### RAG Conventions

- Embedding dimensions are configuration (`Documents:Embeddings:Dimensions`) and must match the `vector(n)` column — changing the embedding model requires a migration and re-embedding.
- Chunking parameters (size, overlap) live in a module Options class, not constants.
- PDFs: only the text layer is read. The upload probe rejects scanned or garbled PDFs before anything is stored; full extraction repeats the check over all pages, records `pages_without_text` and gives passages `PageStart`/`PageEnd`. The parser cannot be cancelled, so both run off the request path with a bounded wait; the probe checks for `startxref` and parses strictly, because lenient parsing scans a whole junk file for minutes.
- The standard HTTP resilience timeouts are raised in `ServiceDefaults` (2 min per attempt, 5 min in all). The module's `Documents:Ingestion:EmbeddingTimeout`, with its own retries, is the deadline that counts; the defaults (10 s, 30 s) failed slow embedding batches with an unexpected error.
- Documents are versioned with stable passage IDs; only the active, indexed version is retrievable, and each answer stores a snapshot of the passages it cited in the `chat` schema (ADR 0004).
- Prompts are built in one place in the Chat module; retrieved chunks are passed with source identifiers so answers can cite them.
- AI provider calls are always made through the `Microsoft.Extensions.AI` abstractions — never reference a provider SDK from a module.
- Integration tests replace `IChatClient` / `IEmbeddingGenerator` with deterministic fakes; never call a real LLM in tests.

## Coding Standards

- **C# 14 features** — primary constructors, collection expressions, `field` keyword, records, pattern matching
- **File-scoped namespaces** — always
- **`var` for obvious types** — explicit types when the type isn't clear from context
- **Naming** — PascalCase public members, `_camelCase` private fields, `Async` suffix on async methods
- **No regions**; comment only the "why"
- **Namespaces** — `RagAssistant.Modules.[Module]...`
- **Internal by default** — handlers, DbContexts, entities, and contract implementations are `internal`; only the module registration extension and endpoint groups are `public`
- **Sealed by default** for classes not designed for inheritance
- **CancellationToken** on every async path, flowing from the endpoint down
- Build uses `TreatWarningsAsErrors` — fix analyzer warnings, don't suppress them (the `tests/.editorconfig` CA1707 exemption for test names is the deliberate exception)

## Testing (TUnit)

- Test projects are `OutputType=Exe` and reference the `TUnit` package; run on Microsoft.Testing.Platform (configured in `global.json`).
- Use `[Test]`, `[Arguments(...)]` for data-driven tests, and `await Assert.That(x).IsEqualTo(y)` — assertions are async and must be awaited.
- Shared fixtures: `[ClassDataSource<T>(Shared = SharedType.PerTestSession)]` for the PostgreSQL Testcontainer and the `WebApplicationFactory<Program>`; implement `IAsyncInitializer` / `IAsyncDisposable` for container lifecycle.
- Use `RagAssistantAppFactory` (`tests/RagAssistant.Integration.Tests/Fixtures/`) via `[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]`; it starts `PostgresFixture` (image `pgvector/pgvector:0.8.7-pg18-trixie`, same as the AppHost — keep both in sync) and runs `DatabaseMigrator`.
- Integration tests require Docker.
- Authorization tests do **not** start Keycloak: `RagAssistantAppFactory` validates tokens signed with a test key (same issuer, audience and `roles` claim as Keycloak); use the persona helper `factory.CreateClientAs(Persona.ContentOwner)` (`tests/RagAssistant.Integration.Tests/Fixtures/TestTokens.cs`) and cover 401 / 403 / success per policy. A single smoke test runs Keycloak via Testcontainers with the same realm file and the same pinned image tag as the AppHost.
- Web tests (`tests/RagAssistant.Web.Tests`) need no Docker or Keycloak: `WebAppFactory` signs a persona in through the real cookie handler (`/test/sign-in`), points YARP at a real Kestrel `StubApi` through the same `services:api` configuration keys Aspire uses, and uses a static OIDC configuration. bUnit runs under TUnit.
- Browser smoke tests (`tests/RagAssistant.Ui.Smoke`, Playwright) drive a **running AppHost** and are run on demand: `dotnet test --project tests/RagAssistant.Ui.Smoke -c Release` (setup in the `ui-smoke-tests` skill). Add or extend a smoke test when a UI spec touches dialogs, file inputs, focus, keyboard or multi-tab behaviour; bUnit cannot catch those. Prefer these over the in-app browser pane for verification, which stops rendering when hidden.
- Test names: `Method_scenario_expected`.
- PDFs in tests are generated with PdfPig's `PdfDocumentBuilder` (`tests/Modules/RagAssistant.Modules.Documents.Tests/TestPdf.cs`, linked into the integration tests; the integration `TestPdfs` adds padded and generated-stream files for size tests). No PDF fixtures are committed. Web upload tests (`UploadBffTests`) run the BFF on real Kestrel, because the in-process test server has no body limit, and are `[NotInParallel]`. `upload.js` itself is covered only by the smoke tests (`PdfImportSmokeTests`), since bUnit has no browser.

## Skills

Load these dotnet-claude-kit skills for context:

- `modern-csharp` — C# 14 language features and idioms
- `vertical-slice` — feature folder structure and handler patterns inside each module
- `project-structure` — solution layout, Directory.Build.props, central package management
- `ef-core` — DbContext patterns, query optimization, migrations (one DbContext per module)
- `minimal-api` / `openapi` / `scalar` — endpoints and API docs
- `caching` — HybridCache
- `dependency-injection` — module-scoped registration
- `error-handling` — Result pattern, ProblemDetails
- `configuration` — Options pattern, per-module configuration sections
- `opentelemetry` / `logging` — observability
- `resilience` / `httpclient-factory` — calls to the AI provider
- `testing` / `tdd` — note: adapt its xUnit examples to TUnit per the Testing section above
- `workflow-mastery`, `instinct-system`

## MCP Tools

> **Setup:** `dotnet tool install -g CWM.RoslynNavigator` (the plugin registers the `cwm-roslyn-navigator` server).

Use `cwm-roslyn-navigator` tools to minimize token consumption:

- `find_symbol` / `get_public_api` before modifying a type
- `find_references` before changing or adding a reference
- `get_project_graph` to verify modules never reference each other
- `find_implementations` instead of grep for interface implementations
- `get_diagnostics` after changes

## Commands

```bash
# Build
dotnet build RagAssistant.slnx

# Run everything (Postgres, pgweb, migrations, api + dashboard) — requires Docker
dotnet run --project src/RagAssistant.AppHost

# Run all tests (Microsoft.Testing.Platform)
dotnet test --solution RagAssistant.slnx

# Run one test project
dotnet test --project tests/RagAssistant.Integration.Tests

# Browser smoke tests (needs the AppHost running with --launch-profile https; see the ui-smoke-tests skill)
dotnet test --project tests/RagAssistant.Ui.Smoke -c Release

# Aspire CLI (needs CLI 13.6.1 or newer): start the AppHost in the background, read a resource's logs, stop it
aspire start --apphost src/RagAssistant.AppHost/RagAssistant.AppHost.csproj
aspire logs api --apphost src/RagAssistant.AppHost/RagAssistant.AppHost.csproj
aspire stop --apphost src/RagAssistant.AppHost/RagAssistant.AppHost.csproj

# Add EF migration for a module
dotnet ef migrations add [Name] --project src/Modules/[Module]/RagAssistant.Modules.[Module] --startup-project src/RagAssistant.Api --context [Module]DbContext

# Format check
dotnet format --verify-no-changes
```

## Workflow

- **Plan first** for any non-trivial task (3+ steps or architecture decisions).
- **Verify before done** — `dotnet build` and `dotnet test` after changes.
- **Stop and re-plan** if an approach goes sideways.
- **Learn from corrections** — capture patterns so the same mistake doesn't recur.

## Anti-patterns

Do NOT generate code that:

- Defines endpoints in Program.cs — use `IEndpointGroup` per feature
- Uses `DateTime.Now` / `UtcNow` — inject `TimeProvider`
- Creates `new HttpClient()` — use `IHttpClientFactory`
- Uses `async void`, `.Result`, or `.Wait()`
- Uses `Results.Ok()` — use `TypedResults`
- Returns entities from endpoints — map to response DTOs
- Wraps EF Core in repository abstractions — use the module's DbContext directly
- Uses the EF in-memory provider or SQLite for tests — use Testcontainers PostgreSQL (pgvector image)
- Calls a real LLM/embedding API from tests
- References a provider SDK (OpenAI, Azure, Ollama…) from a module — only the API knows the provider
- Loads all embeddings into memory to compute similarity — query pgvector with `CosineDistance` + `OrderBy` + `Take`
- Catches `Exception` without a filter or a stated reason (a catch-all that must survive any failure, such as the ingestion processor, is filtered on cancellation and commented with the spec rule it implements)
- Uses string interpolation in log messages, or logs full prompts/documents at Information level
- References another module's project, DbContext, or internal types
- Shares tables between modules or creates a single "god" DbContext
- Puts business logic in `RagAssistant.Shared`
- Uses xUnit/NUnit/MSTest attributes or non-awaited assertions in TUnit tests (ignore MSTest- or xUnit-specific guidance from installed skills, e.g. `writing-mstest-tests` and `code-testing` from `dotnet-test`)
- Adds `Version=` to a `PackageReference` — versions belong in `Directory.Packages.props`
- Sends access tokens to the browser or stores them in browser storage — the Web BFF keeps them server-side
- Identifies users by username or email — use the `sub` claim
- Changes users, roles or clients in the Keycloak admin console — edit the realm file
- Starts Keycloak in ordinary integration tests — use test-key tokens
