# ADR 0008: HybridCache for Query Embeddings

**Status:** Accepted
**Date:** 2026-10-15
**Relates to:** [ADR 0003](0003-model-provider.md), [ADR 0005](0005-api-kernel-libraries.md), [spec 011](../specs/011-retrieval.md)

## Context

Every retrieval call (spec 011) embeds the question through the embedding model. With a local Ollama `bge-m3` this call costs tens to hundreds of milliseconds, and a repeated question (a retry, the same demo question, several users at once) pays it again. CLAUDE.md already names HybridCache as the cache of the stack, but no ADR has added its package, and every new library needs one.

## Decision

- Add `Microsoft.Extensions.Caching.Hybrid`. It is referenced only by the API, which calls `AddHybridCache()`, and by the Documents module tests. The Documents module itself uses the abstract `HybridCache` type, which ships in `Microsoft.Extensions.Caching.Abstractions` with the shared framework, so the module gains no package.
- **In-memory only, no L2.** One API instance is the deployment; a distributed cache would add infrastructure for no measured benefit.
- **First use: query embeddings only.** The key is the embedding model plus a SHA-256 hash of the normalized query. The hash keeps keys under HybridCache's 1,024-character limit and keeps question text out of the keys. Retrieval results and answers are not cached, so a corpus change is visible at once (`product.md`, "never cache an answer in a way that hides a corpus change").

### Alternatives

| Option | Why not |
|---|---|
| `IMemoryCache` | Built in, but no stampede protection: concurrent identical questions would all call the model. No path to an L2 or tag invalidation later. |
| No cache | Simplest, but every retry and every repeated demo question pays the embedding call inside the < 3 s time-to-first-token budget (ADR 0004). |
| Redis (L2) | New infrastructure for a single-instance demo. |

## Consequences

- A failing factory call is not cached, so an embedding outage does not outlive the outage.
- Changing the embedding model changes the key, so old vectors are never reused for another model.
- An L2 can be added later without touching callers.
