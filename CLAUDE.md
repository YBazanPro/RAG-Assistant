# RagAssistant — Modular Monolith

## Project Context

RagAssistant is a .NET 10 demo of a Retrieval-Augmented Generation (RAG) assistant: users ingest documents, the system chunks and embeds them into PostgreSQL + pgvector, and a chat endpoint answers questions grounded in the retrieved chunks.

It is a **modular monolith**: one backend deployable (the Host) composed of independent modules with strict boundaries. Each module owns its features, data (own DbContext + schema), and logic. Modules use **Vertical Slice Architecture** internally (moderate domain complexity, solo developer — keep ceremony low). The UI is a separate Blazor project (`RagAssistant.Web`) acting as a backend for frontend (BFF): it holds the session cookie and forwards `/api/**` (REST + SSE) to the Host through YARP with the user's access token.

Stack decisions are recorded in [ADR 0001](docs/adr/0001-ui-ai-streaming-stack.md) (framework, UI suite, AI layer, streaming, testing) [ADR 0002](docs/adr/0002-identity-and-authentication.md) (identity and authentication), [ADR 0003](docs/adr/0003-model-provider.md) (model provider) and [ADR 0004](docs/adr/0004-document-versioning.md) (document versioning, citation snapshots, targets, retention) and [ADR 0005](docs/adr/0005-api-kernel-libraries.md) (API kernel libraries, Mediator topology, no Serilog).

### Planned Modules

| Module | Owns | Schema |
|--------|------|--------|
| `Documents` | Upload/ingest documents, text extraction, chunking, embedding generation, vector storage, similarity search | `documents` |
| `Chat` | Conversations and messages, prompt building, retrieval orchestration, LLM answer generation with citations | `chat` |

Modules are added with `/scaffold` — none exist yet.

## Tech Stack

- **.NET 10** (LTS) / C# 14 — do not move to .NET 11 (STS)
- **.NET Aspire 13.6.1** — dev-only orchestration (AppHost, ServiceDefaults, dashboard); see [spec 001](docs/specs/001-aspire-postgres.md)
- **ASP.NET Core Minimal APIs** — `IEndpointGroup` per feature with `app.MapEndpoints()` auto-discovery
- **Entity Framework Core** + **Npgsql** — one DbContext per module, PostgreSQL
- **pgvector** (`Pgvector.EntityFrameworkCore`) — embedding storage and similarity search (`vector` columns, HNSW index, cosine distance)
- **Microsoft.Extensions.AI** — `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` abstractions; the concrete provider is wired only in the Host: local Ollama in the AppHost by default (multilingual embeddings, `bge-m3` / 1024 dims initially) plus a deterministic fake-chat demo mode (ADR 0003). The answer flow is a fixed pipeline in handler code — **Microsoft Agent Framework and AG-UI are deferred** (ADR 0001)
- **Server-Sent Events** — native .NET 10 `TypedResults.ServerSentEvents` with typed event DTOs for answer streaming and ingestion progress; clients parse with `SseParser`
- **Blazor Web App** (`RagAssistant.Web`) — `InteractiveWebAssembly` for streaming pages, prerendering on
- **Fluent UI Blazor** (`Microsoft.FluentUI.AspNetCore.Components`) — the only component suite; RAG domain components are app-owned. No Syncfusion or second design system
- **Background ingestion** — `BackgroundService` + `Channel<T>`, stage state persisted in the `documents` schema
- **HybridCache** — caching (e.g. query embeddings, retrieval results); no L2 backend yet
- **Mediator** (source-generated, MIT) — intra-module command/query dispatch; one scoped Mediator per module with `ValidationBehavior`, inject the module's concrete generated `Mediator` (aliased, e.g. `ModuleMediator`), never `IMediator`/`ISender` (ADR 0005)
- **FluentValidation** — request validation
- **Logging** — built-in `ILogger` + OpenTelemetry (logs, traces, metrics) via ServiceDefaults; no Serilog (ADR 0005)
- **TUnit** + **WebApplicationFactory** + **Testcontainers (PostgreSQL)** — testing, on Microsoft.Testing.Platform; **bUnit** for Blazor components
- **Microsoft.Extensions.AI.Evaluation** — answer-quality evaluation in a separate harness, never in `dotnet test`
- **Auth** — Keycloak (dev-only container, `Aspire.Hosting.Keycloak`, preview) configured entirely by the committed realm file `src/RagAssistant.AppHost/Realms/ragassistant-realm.json` (no data volume — re-imported every run). Host: JWT bearer (`AddKeycloakJwtBearer`, audience `ragassistant-api`, `RoleClaimType = "roles"`). Web: OIDC code + PKCE, cookie session, tokens never sent to the browser. Roles `knowledge-seeker` ⊂ `content-owner` ⊂ `maintainer`; policies `KnowledgeSeeker`, `ContentOwner`, `Maintainer` (names in `RagAssistant.Shared/Common/Security`); a fallback policy requires an authenticated user on every endpoint, so opt out explicitly with `AllowAnonymous`. Realm clients: `ragassistant-web` (BFF), `ragassistant-api` (audience), `ragassistant-dev` (public, password grant, dev tokens and the smoke test). Claims are not remapped (`sub`, `roles` as issued)
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
  RagAssistant.Host/                     # Startup host — wires modules, AI providers, middleware
    Program.cs
    appsettings.json
  RagAssistant.Web/                      # Blazor Web App (Fluent UI) + BFF: OIDC sign-in, YARP to the Host (planned)

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

- Never call `Database.MigrateAsync()` from the Host. Each module exposes a public `AddXxxPersistence(IServiceCollection, IConfiguration)` that registers its DbContext and calls `services.AddMigrationTarget<XxxDbContext>()`; `AddXxxModule` calls it too.
- Register every module's `AddXxxPersistence` in `src/RagAssistant.MigrationService/Program.cs`. The AppHost runs the MigrationService before the Host (`WaitForCompletion`); a failed migration keeps the Host from starting.
- The integration-test fixture runs the same `DatabaseMigrator`, so register persistence in the Host via `AddXxxModule` and tests pick it up.
- Dev Postgres: image `pgvector/pgvector:0.8.7-pg18-trixie`, `localhost:5433`, persistent container + volume `ragassistant-postgres-data` mounted at `/var/lib/postgresql` (PG18 layout — don't switch to `WithDataVolume`). The password is generated by Aspire into the AppHost user secrets (`Parameters:postgres-password`).

### RAG Conventions

- Embedding dimensions are configuration (`Documents:Embeddings:Dimensions`) and must match the `vector(n)` column — changing the embedding model requires a migration and re-embedding.
- Chunking parameters (size, overlap) live in a module Options class, not constants.
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
- Test names: `Method_scenario_expected`.

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

# Run everything (Postgres, pgweb, migrations, host + dashboard) — requires Docker
dotnet run --project src/RagAssistant.AppHost

# Run all tests (Microsoft.Testing.Platform)
dotnet test --solution RagAssistant.slnx

# Run one test project
dotnet test --project tests/RagAssistant.Integration.Tests

# Add EF migration for a module
dotnet ef migrations add [Name] --project src/Modules/[Module]/RagAssistant.Modules.[Module] --startup-project src/RagAssistant.Host --context [Module]DbContext

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
- References a provider SDK (OpenAI, Azure, Ollama…) from a module — only the Host knows the provider
- Loads all embeddings into memory to compute similarity — query pgvector with `CosineDistance` + `OrderBy` + `Take`
- Catches bare `Exception`
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
