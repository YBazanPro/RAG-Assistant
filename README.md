# RAG-Assistant

A demo that shows **how to build a small Retrieval-Augmented Generation (RAG) assistant** with modern .NET, and how to build it **with an AI coding agent under explicit project rules**.

Users import documents (Markdown, plain text, PDF). The system chunks them, embeds the chunks into PostgreSQL + pgvector, and a chat answers questions from the retrieved passages, in English or French. Every answer shows the passages it cites, and the app says so when the documents do not contain an answer.

> **Status:** first release (spec 020). Documents, retrieval (vector, optional hybrid search and reranking), grounded chat with follow-ups, citations that show when their source changed, feedback, maintainers' diagnostics and an evaluation harness whose runs show in the admin area. See the [roadmap](docs/roadmap.md).

## Goals

This repository is a teaching artifact before it is a product. It aims to show:

1. **A RAG pipeline you can read end to end**: ingestion, chunking, embeddings, vector and keyword search, grounded and cited answers, honest refusals. It uses the `Microsoft.Extensions.AI` abstractions, with no provider SDK in the business code and no agent framework hiding the flow.
2. **A modular monolith in .NET 10**: independent modules with their own schema, DbContext and migrations, vertical slices inside each module, and communication only through small contracts.
3. **.NET Aspire 13.6 as the development orchestrator**: one command starts PostgreSQL, Keycloak, Ollama, migrations, the API and the web app, with the dashboard for logs, traces and metrics.
4. **AI-assisted development done deliberately**: specifications before code, architecture decisions recorded as ADRs, rules in `CLAUDE.md`, and tests, reviews and CI as the safety net. The code is written with [Claude](https://claude.com/claude-code) and the [dotnet-claude-kit](https://github.com/codewithmukesh/dotnet-claude-kit) skills, and the process is part of what the repo demonstrates.
5. **Trust in the interface**: streaming answers, visible citations, processing and failure states, a refusal path that needs no model, and measured quality (an evaluation harness with a golden dataset).

It is intentionally **small and minimal in dependencies**: every library must earn its place and has an ADR when it is not part of .NET, ASP.NET Core, EF Core or PostgreSQL.

## Technologies

| Area | Choice |
|------|--------|
| Platform | .NET 10 (LTS), C# 14, ASP.NET Core Minimal APIs |
| Orchestration (dev) | .NET Aspire 13.6 (AppHost, ServiceDefaults, dashboard, Aspire CLI) |
| Data | PostgreSQL 18 + pgvector (HNSW, cosine distance), full-text search, EF Core + Npgsql, one schema per module |
| AI | `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`); local Ollama by default, an opt-in hosted OpenAI-compatible chat model, a deterministic demo chat client |
| Reranking (optional) | Text Embeddings Inference with `BAAI/bge-reranker-v2-m3` |
| Streaming | Server-Sent Events (`TypedResults.ServerSentEvents`) |
| UI | Blazor Web App (WebAssembly) + Fluent UI Blazor, behind a BFF (YARP) |
| Identity | Keycloak (dev container), OIDC code + PKCE, JWT bearer, three roles |
| PDF | PdfPig (text layer only, no OCR) |
| Tests | TUnit on Microsoft.Testing.Platform, WebApplicationFactory, Testcontainers, bUnit, Playwright smoke tests |
| Evaluation | `Microsoft.Extensions.AI.Evaluation`, a console harness, nightly in CI |

The reasoning behind each choice is in the [ADRs](docs/adr/).

## Architecture at a glance

```text
Browser ──► RagAssistant.Web (Blazor + BFF: cookie session, YARP) ──► RagAssistant.Api
                                                                        ├─ Documents module   (schema: documents)
                                                                        ├─ Chat module        (schema: chat)
                                                                        └─ Evaluation module  (schema: evaluation)
Aspire AppHost: postgres (pgvector) · pgweb · keycloak · ollama · migrations · api · web  [· reranker]
```

- **Modular monolith**: one deployable API, composed of modules that never reference each other. Cross-module capabilities are interfaces and DTOs in `RagAssistant.SharedKernel/Contracts`.
- **Vertical slices**: each feature is one file with its command or query, handler, response and validator.
- **Background ingestion**: `BackgroundService` + `Channel<T>`, with the stage state persisted so unfinished runs resume after a restart.
- **Versioned documents**: stable passage IDs, only the active indexed version is searchable, and each answer stores a snapshot of the passages it cited, so old answers keep their sources and say when a newer version changed them.

| Module | Responsibility |
|--------|----------------|
| `Documents` | Import, extraction, chunking, embedding, versions, private and shared documents, retrieval (vector, hybrid, rerank), suggested questions |
| `Chat` | Conversations, follow-up rewriting, prompt building, streamed cited answers, feedback, request traces, export |
| `Evaluation` | Evaluation runs uploaded by the harness, for the admin area |

## Project documentation

| Document | What it answers |
|----------|-----------------|
| [Product overview](docs/product.md) | What is being built and why, principles, user journeys |
| [Roadmap](docs/roadmap.md) | Phases, order of delivery, what is done |
| [Specs](docs/specs/) | One specification per feature, with acceptance criteria |
| [ADRs](docs/adr/) | Stack, identity, model provider, versioning, kernel libraries, PDF import, browser tests, caching, visibility, reranker |
| [CLAUDE.md](CLAUDE.md) | The rules the AI assistant follows in this repo |

## How this repo is built

Every feature follows the same loop: **spec → plan → tests first → implement → verify → review → CI**. Specs and ADRs are committed before or with the code, so the history reads as a design log.

## Prerequisites

- .NET SDK 10
- Docker (running), with about 10 GB free for images and models
- Optional: the [Aspire CLI](https://aspire.dev) 13.6.1 (`aspire start`, `logs`, `stop`); `dotnet run` works without it
- Optional, to work on the project the way it was built: [Claude Code](https://claude.com/claude-code) with the dotnet-claude-kit plugin

The default model provider is a local Ollama container started by the AppHost, so no API key is needed; the first run downloads the models (`bge-m3` for embeddings, `ministral-3:8b` for answers). See [ADR 0003](docs/adr/0003-model-provider.md).

**GPU note.** The AppHost's Ollama container only uses an NVIDIA GPU. On a machine without one (the reference laptop of [spec 018](docs/specs/018-evaluation-and-french-english.md) has an integrated Radeon the container cannot use) every model runs on the CPU: the first token of an answer then takes several seconds. For a quick demo, use the `https-demo` profile (no chat model) or a smaller model:

```bash
dotnet run --project src/RagAssistant.AppHost -- --Parameters:chat-model=llama3.2:3b
```

## Run

```bash
dotnet run --project src/RagAssistant.AppHost
```

Open the dashboard via the login URL printed in the console, then the `web` link (`https://localhost:7053`).

| Launch profile | What it starts | Use it for |
|----------------|----------------|------------|
| `https` (default) | Everything, with the Ollama chat model | The real thing |
| `https-demo` | Same, but answers come from a deterministic extractive demo client; no chat model is pulled, and no suggested questions are generated | A fast demo on any machine, the UI smoke tests |
| `https-rerank` | Same as `https`, plus the TEI reranker container (spec 019, [ADR 0010](docs/adr/0010-text-embeddings-inference-reranker.md)); hybrid search and reranking are switched on | Measuring retrieval; on a CPU raise the rerank timeout (see `CLAUDE.md`) |

```bash
dotnet run --project src/RagAssistant.AppHost --launch-profile https-demo
```

The AppHost starts:

| Resource | What | Where |
|----------|------|-------|
| `postgres` | PostgreSQL 18 + pgvector 0.8.7 (`pgvector/pgvector:0.8.7-pg18-trixie`) | `localhost:5433` |
| `pgweb` | Database browser | link in the dashboard |
| `ollama` | Local model server, models kept in the `ragassistant-ollama-models` volume | — |
| `migrations` | Applies the EF Core migrations of every module, then exits | — |
| `keycloak` | Identity provider (dev only), realm imported from `src/RagAssistant.AppHost/Realms/ragassistant-realm.json` | `https://localhost:8180` |
| `api` | The API (starts after migrations and Keycloak) | `https://localhost:7043` |
| `web` | The UI and its backend for frontend: sign-in, session cookie, `/api/**` forwarded to the API | `https://localhost:7053` |
| `reranker` | Only with `https-rerank` | — |

The `postgres` and `pgweb` containers are persistent: they keep running after the AppHost stops, and data lives in the Docker volume `ragassistant-postgres-data`. Original PDF files live in `.data/documents` (git-ignored); back them up with the database.

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

Keycloak has no data volume: the realm file is re-imported on every run, so changes made in the Keycloak admin console are lost. Edit the realm file instead.

Dev-only users (the realm file is never deployed):

| Username | Password | Role | What they can do |
|----------|----------|------|------------------|
| `seeker` | `seeker-dev` | Knowledge Seeker | Ask questions, browse shared documents |
| `owner` | `owner-dev` | Content Owner | Also import documents and manage the ones they imported (private or shared) |
| `maintainer` | `maintainer-dev` | System Maintainer | Also the Admin page: requests, ingestion failures, feedback, evaluation runs, configuration |

Sign out first to switch users: sign-out also ends the Keycloak session. The UI is English only (the documents and answers may be French) and has a System / Light / Dark theme choice in the menu on the user name.

The browser only calls the Web, which forwards `/api/**` to the API with your access token (tokens never reach the browser). To call the API directly, get a token with the public dev client `ragassistant-dev`:

```bash
curl -sk -X POST https://localhost:8180/realms/ragassistant/protocol/openid-connect/token -d grant_type=password -d client_id=ragassistant-dev -d username=maintainer -d password=maintainer-dev
```

## Load the sample corpus

The evaluation harness imports 17 sample documents (9 English, 8 French; Markdown and PDF) from `samples/corpus`, tagged `sample`. With the app running:

```bash
dotnet run --project tools/RagAssistant.Evaluation -- seed
```

Running it again only updates what changed.

## Demo script

With the sample corpus loaded, sign in as `owner` (about ten minutes):

1. **Home.** The page counts the documents per language, lists the latest ones and (outside demo mode) suggests questions the documents answer. Click one: a new conversation opens and asks it.
2. **Ask in English.** *How long do I have to submit an expense claim after a trip?* The answer streams with `[1]` markers; open a citation to see the passage it comes from, and **Activity** to see what retrieval found.
3. **Follow up.** *What is the hotel limit per night?* then *And in London?*: the second question is rewritten into a standalone one (shown as "Searched for").
4. **Ask in French.** *Combien de jours de congés payés un salarié acquiert-il par mois ?* The answer is in French, from the French policy.
5. **Cross-language.** *How many days of paid leave does an employee earn per month?*: an English answer from the French document.
6. **Refusal.** *What is the capital of Australia?* The app says the documents do not answer it, without calling the model.
7. **A changed source.** Open the cited document, upload a new version or reprocess it, and come back to the conversation: the citation now says *Newer version* or *Changed in newer version*, and its preview offers the current version.
8. **Keep it.** Rename the conversation, search for it in the list (accents and case do not matter), **Export as Markdown**, or **Copy answer with sources**.
9. **Maintainer.** Sign in as `maintainer`, open **Admin**: the request inspector shows each retrieval stage; **Evaluation** lists the harness runs.

## Test

```bash
dotnet test --solution RagAssistant.slnx
```

The Web tests (`tests/RagAssistant.Web.Tests`) need no Docker: bUnit component tests and the BFF against an in-process stub API. Integration tests start their own PostgreSQL container through Testcontainers, so Docker must be running. No test calls a real model.

### Browser smoke tests

Playwright tests in `tests/RagAssistant.Ui.Smoke` drive a running app and are not part of the solution. Start the app in demo mode, install Chromium once, then run them:

```bash
dotnet run -c Release --project src/RagAssistant.AppHost --launch-profile https-demo
```

```bash
powershell -NoProfile -File tests/RagAssistant.Ui.Smoke/bin/Release/net10.0/playwright.ps1 install chromium
```

```bash
dotnet test --project tests/RagAssistant.Ui.Smoke -c Release
```

## Evaluation harness

`tools/RagAssistant.Evaluation` measures retrieval and answers against the golden questions in `samples/golden` (spec 018). With the app running:

```bash
dotnet run --project tools/RagAssistant.Evaluation -- run
```

It writes `report.json` and `report.md` to `.data/evaluation/<run>/` and uploads the report to the app, where maintainers see it under **Admin › Evaluation** (`--no-upload` keeps it local; `upload <report>` sends an older one). `compare`, `sweep` and `judge` work on reports offline; see `CLAUDE.md` for every command. The same run happens nightly in CI with a small model (`.github/workflows/evaluation.yml`).
