# ADR 0003: Model Provider

> **Naming note (2026-10-15):** the backend project `RagAssistant.Host` has since been renamed `RagAssistant.Api` (Aspire resource `api`, configuration keys `services:api:*`, test stub `StubApi`). "The Host" in this document means that project.

**Status:** Accepted
**Date:** 2026-10-13
**Relates to:** [ADR 0001](0001-ui-ai-streaming-stack.md) (AI layer), [roadmap](../roadmap.md) decision D1

## Context

The assistant needs a chat model and an embedding model. The embedding model fixes the `vector(n)` column size and must handle French and English. The project must stay "clone and run" without paid accounts or API keys (see [ADR 0002](0002-identity-and-authentication.md) for the same rule applied to identity).

> **Note ([spec 005](../specs/005-web-shell.md)):** the product is now English only, so the French requirement above no longer applies. `bge-m3` stays the initial choice; nothing else in this decision changes.

ADR 0001 fixed the abstractions (`IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`); only the concrete provider was open.

## Decisions

### 1. Default provider: local Ollama, run by the AppHost

An Ollama container is added to the AppHost (Aspire Community Toolkit). The Host registers the Microsoft.Extensions.AI clients against it. No API key is needed.

- **Embedding model:** a multilingual model (initial choice `bge-m3`, 1024 dimensions). `Documents:Embeddings:Dimensions` is set to match, and the `vector(n)` column follows.
- **Chat model:** a small instruction-tuned model chosen at spec time for 3.3; it is configuration, not code.
- Model names and dimensions live in configuration. Changing the embedding model requires a migration and re-embedding (CLAUDE.md, RAG conventions).

### 2. Demo mode: deterministic fake chat client

A configuration switch registers a fake `IChatClient` that streams a scripted answer with citations, so the UI runs with no model download at all. Embeddings still come from the real embedding model, because retrieval must be real. Demo mode is for demos and UI work, never for evaluation.

### 3. Provider wiring stays in the Host

Modules depend only on the Microsoft.Extensions.AI abstractions. Only `RagAssistant.Host` references provider packages. Swapping to a hosted provider (Azure OpenAI, OpenAI) is a Host registration change plus, if the embedding model changes, the dimension migration above.

## Consequences

- Clone, run the AppHost, wait for the first model pull, and the app works offline.
- Local model quality is lower than hosted models. Phase 5 evaluation measures it, and a hosted provider remains an option.
- The first run downloads model weights (size depends on the models chosen); the README documents this.
- Adding the Ollama integration packages is a new dependency, accepted by this ADR.

## Rejected

| Option | Reason |
|---|---|
| Azure OpenAI / OpenAI as default | Requires an account and keys; breaks clone-and-run. Remains a supported swap. |
| Anthropic for chat | No embeddings API, so a second provider would be needed anyway. |
