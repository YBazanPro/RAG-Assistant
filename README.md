# RAG-Assistant

A demo that shows **how to build a small Retrieval-Augmented Generation (RAG) assistant** with modern .NET, and how to build it **with an AI coding agent under explicit project rules**.

Users import documents (Markdown, plain text, PDF). The system chunks them, embeds the chunks into PostgreSQL + pgvector, and a chat endpoint answers questions grounded in the retrieved passages. It shows the evidence behind every answer, and it says so when the corpus does not contain one.

> **Status:** work in progress. The Documents side is complete (import, background ingestion with live progress, browsing, versioning, PDF). Retrieval and the Chat module are next. See the [roadmap](docs/roadmap.md).

## Goals

This repository is a teaching artifact before it is a product. It aims to show:

1. **A RAG pipeline you can read end to end**: ingestion, chunking, embeddings, vector search, grounded and cited answers, honest refusals. It uses the `Microsoft.Extensions.AI` abstractions, with no provider SDK in the business code and no agent framework hiding the flow.
2. **A modular monolith in .NET 10**: independent modules with their own schema, DbContext and migrations, vertical slices inside each module, and communication only through small contracts.
3. **.NET Aspire 13.6 as the development orchestrator**: one command starts PostgreSQL, Keycloak, migrations, the API and the web app, with the dashboard for logs, traces and metrics.
4. **AI-assisted development done deliberately**: specifications before code, architecture decisions recorded as ADRs, rules in `CLAUDE.md`, and tests, reviews and CI as the safety net. The code is written with [Claude](https://claude.com/claude-code) and the [dotnet-claude-kit](https://github.com/codewithmukesh/dotnet-claude-kit) skills, and the process is part of what the repo demonstrates.
5. **Trust in the interface**: streaming answers, visible citations, processing and failure states, and a refusal path that needs no model.

It is intentionally **small and minimal in dependencies**: every library must earn its place and has an ADR when it is not part of .NET, ASP.NET Core, EF Core or PostgreSQL.

## Technologies

| Area | Choice |
|------|--------|
| Platform | .NET 10 (LTS), C# 14, ASP.NET Core Minimal APIs |
| Orchestration (dev) | .NET Aspire 13.6 (AppHost, ServiceDefaults, dashboard, Aspire CLI) |
| Data | PostgreSQL 18 + pgvector (HNSW, cosine distance), EF Core + Npgsql, one schema per module |
| AI | `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`); local Ollama by default, deterministic fake chat demo mode |
| Streaming | Server-Sent Events (`TypedResults.ServerSentEvents`) |
| UI | Blazor Web App (WebAssembly) + Fluent UI Blazor, behind a BFF (YARP) |
| Identity | Keycloak (dev container), OIDC code + PKCE, JWT bearer, three roles |
| PDF | PdfPig (text layer only, no OCR) |
| Tests | TUnit on Microsoft.Testing.Platform, WebApplicationFactory, Testcontainers, bUnit, Playwright smoke tests |
| AI-assisted workflow | Claude Code, dotnet-claude-kit, project skills in `.claude/skills` |

The reasoning behind each choice is in the [ADRs](docs/adr/).

## Architecture at a glance

```text
Browser ──► RagAssistant.Web (Blazor + BFF: cookie session, YARP) ──► RagAssistant.Api
                                                                        ├─ Documents module  (schema: documents)
                                                                        └─ Chat module       (schema: chat, not started)
Aspire AppHost: postgres (pgvector) · pgweb · keycloak · migrations · api · web
```

- **Modular monolith**: one deployable API, composed of modules that never reference each other. Cross-module capabilities are interfaces and DTOs in `RagAssistant.SharedKernel/Contracts`.
- **Vertical slices**: each feature is one file with its command or query, handler, response and validator.
- **Background ingestion**: `BackgroundService` + `Channel<T>`, with the stage state persisted so unfinished runs resume after a restart.
- **Versioned documents**: stable passage IDs, only the active indexed version is searchable, and each answer will store a snapshot of the passages it cited.

| Module | Responsibility | State |
|--------|----------------|-------|
| `Documents` | Import, extraction, chunking, embedding, vector storage, browsing, lifecycle | Import to lifecycle done, search to come |
| `Chat` | Conversations, retrieval orchestration, prompt building, cited answers | Not started |

## Project documentation

| Document | What it answers |
|----------|-----------------|
| [Product overview](docs/product.md) | What is being built and why, principles, user journeys |
| [Roadmap](docs/roadmap.md) | Phases, order of delivery, what is done |
| [Specs](docs/specs/) | One specification per feature, with acceptance criteria |
| [ADRs](docs/adr/) | Stack, identity, model provider, versioning, kernel libraries, PDF import, browser tests |
| [CLAUDE.md](CLAUDE.md) | The rules the AI assistant follows in this repo |

## How this repo is built (and the article series)

Every feature follows the same loop: **spec → plan → tests first → implement → verify → review → CI**. Specs and ADRs are committed before or with the code, so the history reads as a design log.

A series of articles will follow, explaining the architecture, the features, and how the project was designed and implemented, including how Claude and dotnet-claude-kit were used. The specs, ADRs and the roadmap are the source material for it; the git history shows the order in which things happened.

## Prerequisites

- .NET SDK 10
- Docker (running)
- Optional: the [Aspire CLI](https://aspire.dev) 13.6.1 (`aspire start`, `logs`, `stop`); `dotnet run` works without it
- Optional, to work on the project the way it was built: [Claude Code](https://claude.com/claude-code) with the dotnet-claude-kit plugin

The default model provider is a local Ollama container started by the AppHost, so no API key is needed (the first run downloads the models). See [ADR 0003](docs/adr/0003-model-provider.md).

- .NET SDK 10
- Docker (running)

## Run

```bash
dotnet run --project src/RagAssistant.AppHost
```

Open the dashboard via the login URL printed in the console. The AppHost starts:

| Resource | What | Where |
|----------|------|-------|
| `postgres` | PostgreSQL 18 + pgvector 0.8.7 (`pgvector/pgvector:0.8.7-pg18-trixie`) | `localhost:5433` |
| `ragassistant` | Application database | — |
| `pgweb` | Database browser | link in the dashboard |
| `ollama` | Local model server with the embedding model (`bge-m3`), models kept in the `ragassistant-ollama-models` volume | — |
| `migrations` | Applies EF Core migrations, then exits | — |
| `keycloak` | Identity provider (dev only, `quay.io/keycloak/keycloak:26.8`), realm imported from `src/RagAssistant.AppHost/Realms/ragassistant-realm.json` | `https://localhost:8180` (Aspire dev certificate) |
| `api` | The API (starts after migrations and Keycloak) | `https://localhost:7043` |
| `web` | The UI and its backend for frontend (starts after the API and Keycloak): sign-in, session cookie, `/api/**` forwarded to the API | link in the dashboard |

The `postgres` and `pgweb` containers are persistent: they keep running after the AppHost stops, and data lives in the Docker volume `ragassistant-postgres-data`.

### Database credentials

User `postgres`; the password is generated by Aspire on first run and stored in the AppHost user secrets:

```bash
dotnet user-secrets list --project src/RagAssistant.AppHost
```

If that secret is lost or changed, the existing volume still expects the old password and the app can't connect. Either restore the secret, or reset the dev database (this deletes all local data):

```bash
docker rm -f $(docker ps -aq --filter "publish=5433")
```

```bash
docker volume rm ragassistant-postgres-data
```

## Sign-in and test users

Keycloak has no data volume: the realm file is re-imported on every run, so changes made in the Keycloak admin console are lost. Edit the realm file instead. The admin console password is generated by Aspire into the AppHost user secrets (`Parameters:keycloak-password`).

Dev-only users (the realm file is never deployed):

| Username | Password | Role | Effective `roles` claim |
|----------|----------|------|-------------------------|
| `seeker` | `seeker-dev` | Knowledge Seeker | `knowledge-seeker` |
| `owner` | `owner-dev` | Content Owner | `content-owner`, `knowledge-seeker` |
| `maintainer` | `maintainer-dev` | System Maintainer | `maintainer`, `content-owner`, `knowledge-seeker` |

Open the `web` link from the dashboard and sign in with one of these users (sign out first to switch: sign-out also ends the Keycloak session, so the next sign-in asks for credentials again). Only the maintainer sees the Admin page. The UI is English only and has a System / Light / Dark theme toggle.

The browser only calls the Web, which forwards `/api/**` to the API with your access token (tokens never reach the browser). Every `/api` endpoint on the API requires a bearer token. To call the API directly, get a token by hand with the public dev client `ragassistant-dev` (password grant):

```bash
curl -sk -X POST https://localhost:8180/realms/ragassistant/protocol/openid-connect/token -d grant_type=password -d client_id=ragassistant-dev -d username=maintainer -d password=maintainer-dev
```

Keycloak is served over HTTPS with the Aspire development certificate, hence `curl -k`. Send the returned `access_token` as `Authorization: Bearer <token>`. Tokens last one hour.

## Test

```bash
dotnet test --solution RagAssistant.slnx
```

The Web tests (`tests/RagAssistant.Web.Tests`) need no Docker: they run bUnit component tests and exercise the BFF against an in-process stub API.

Integration tests start their own throwaway PostgreSQL container (same image) through Testcontainers, so Docker must be running. They validate tokens signed with a test key, so only one smoke test starts Keycloak (same image and realm file as the AppHost).
