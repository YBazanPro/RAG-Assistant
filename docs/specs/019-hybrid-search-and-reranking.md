# Spec 019: Hybrid search and reranking

**Status:** Approved (2026-10-18)
**Date:** 2026-10-18
**Roadmap:** "Later": hybrid search, reranking · **Builds on:** specs [011](011-retrieval.md), [012](012-chat-conversations-and-grounded-answers.md), [015](015-chat-scope-activity-and-layout.md), [016](016-private-and-shared-documents.md), [017](017-diagnose-traces-admin-and-metrics.md), [018](018-evaluation-and-french-english.md) · **Decision records:** ADR 0010 (new: Text Embeddings Inference for reranking)

**Prerequisite:** spec 018 is implemented. Its harness, sample corpus and recalibrated thresholds are the baseline here, and nothing in this spec can be accepted without them.

## Problem

Retrieval is vector-only (spec 011). Dense embeddings miss exact terms such as policy codes, amounts, acronyms and product names, and cosine order is a coarse relevance signal. The roadmap deferred both fixes until evaluation could show they help. Spec 018 provides that measurement. This spec adds both stages behind switches and keeps each one only if the harness shows a gain without hurting refusals or latency.

Success: the harness compares vector, hybrid, and hybrid + rerank on the same run. Each stage's default is set from that report, and a maintainer can see, for every passage, which stage found it and how it scored.

## Scope

### In

**Hybrid search (Documents module)**

- **Full-text index per passage.** `passages` gets:
  - `search_config` (`regconfig`): `english` for `en`, `french_unaccent` for `fr`, `simple` otherwise;
  - `search_vector` (`tsvector`), a stored generated column `to_tsvector(search_config, text)` with a GIN index.
  The `french_unaccent` configuration (French stemmer plus `unaccent`) and the `unaccent` extension are created by the Documents migration. The generated column backfills existing passages.
- **The passage's config follows its document's language.** When a document's language changes (edit, detection, spec 018), its passages' `search_config` is updated in the same transaction.
- **Keyword query in the question's written language.** The config follows the detected question language from spec 018 (with its conversation fallback), never the requested answer language. `websearch_to_tsquery(config, query)`, ranked with `ts_rank_cd`. `IDocumentSearch` receives the language through `SearchFilters.QueryLanguage` (nullable → `simple`). French questions therefore get keyword matches from French passages only. Vector search still covers every language.
- **Reciprocal rank fusion.** The vector pool and the keyword pool (same pool size, same filters and `ReadableBy`) are merged with `score = Σ 1 / (k + rank)`, where `k = Documents:Retrieval:Hybrid:RrfK` (default 60).
- **Switch:** `Documents:Retrieval:Hybrid:Enabled`.

**Reranking**

- **Cross-encoder `bge-reranker-v2-m3` (multilingual)** served by Hugging Face **Text Embeddings Inference (TEI)**. ADR 0010 records the choice (new infrastructure) and the pinned image tag, CPU or GPU variant.
- **Contract:** `IPassageReranker` in `RagAssistant.SharedKernel/Contracts/Retrieval`: `RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken)` → one relevance score per text. The API registers the TEI implementation (`IHttpClientFactory`, Aspire service discovery). Documents only knows the interface, so only the API knows the provider (CLAUDE.md).
- **Flow:**
  1. the top `Documents:Retrieval:Reranking:Candidates` fused (or vector) candidates, default 30, go to TEI in one batch;
  2. they are reordered by rerank score;
  3. near-duplicate reduction and `topK` apply after reranking.
- **Evidence gate:**
  - **Reranking on:** `MinScore` and `ConfidentScore` apply to the rerank score. Their settings are `Documents:Retrieval:Reranking:MinScore` and `…:ConfidentScore`, calibrated by the sweep.
  - **Reranking off or skipped:** cosine is the gate, as today. A passage found only by keywords is admitted when its cosine is ≥ `MinScore`.
- **Fallback:** on timeout (`Documents:Retrieval:Reranking:Timeout`, default 2 s), an error or an unreachable TEI, retrieval continues without reranking and is gated by cosine. The trace, activity and metrics record `rerank: skipped (unavailable)`. A question never fails because of the reranker.
- **Switch:** `Documents:Retrieval:Reranking:Enabled`. The AppHost adds the TEI resource only with the launch profile `https-rerank` (or the parameter `reranking=true`), which also sets the switch. The default clone-and-run is unchanged.

**Diagnostics and evaluation**

- **Per-passage stage data:**
  - vector rank and cosine;
  - keyword rank and `ts_rank_cd`;
  - RRF score;
  - rerank score;
  - `admittedBy` (`vector` \| `keyword` \| `both`).
  It is stored in the request trace and shown in `GET /api/documents/search`, in the admin request inspector, and compactly in the activity panel.
- **Per-call overrides** on `GET /api/documents/search` (maintainer): `mode=vector|hybrid` and `rerank=true|false`. `candidates=true` (spec 018) also returns the stage data and rerank scores.
- **Harness:**
  - `run --variants` measures retrieval for vector, hybrid and hybrid + rerank in one run through the overrides; answer metrics remain one run per configuration;
  - `sweep` also calibrates the rerank thresholds;
  - reports record `mode`, `rerank`, `RrfK`, `Candidates` and the reranker model.
- **Gate for defaults:** a stage is switched on by default when the harness shows:
  - overall recall@5 or MRR improved by ≥ 3 points;
  - refusal correctness down by ≤ 2 points in each language;
  - median time to first token up by ≤ 300 ms (hybrid) or ≤ 800 ms (reranking).
  Otherwise the stage ships switched off. The report and decision go into this spec's *Calibration* section.

### Out

- Boosting passages in the question's language, or keyword search across both language configs.
- Query expansion, translation, synonyms dictionaries.
- Reranking with the chat model; a reranker inside Ollama or llama.cpp.
- Caching rerank results (answers and retrieval results are never cached, spec 011 / ADR 0008).
- UI controls for mode or rerank in chat; overrides are for maintainers on the diagnostic endpoint only.
- A second TEI use (embeddings stay on Ollama `bge-m3`).

## Domain Model

| Entity / object | Change |
|---|---|
| `Passage` (`documents.passages`) | `search_config regconfig NOT NULL` (from the document language), `search_vector tsvector` generated stored, GIN index. |
| Migration | `CREATE EXTENSION unaccent`; `CREATE TEXT SEARCH CONFIGURATION french_unaccent (COPY = french)` with `unaccent, french_stem` for word tokens; backfill `search_config` from each passage's document language. |
| `SearchFilters` (contract) | `QueryLanguage` (two letters, nullable). |
| `RetrievedChunk` (contract) | `Stages` record: `VectorRank`, `Cosine`, `KeywordRank`, `KeywordScore`, `FusedScore`, `RerankScore` (all nullable), `AdmittedBy`. `Score` stays the gating score (rerank score when reranked, else cosine). |
| `SearchResult` (contract) | `Mode` (`vector` \| `hybrid`), `Rerank` (`applied` \| `off` \| `skipped`). |
| `RequestTrace` / trace passages (`chat`) | Stage fields per passage; `RetrievalMode`, `RerankStatus`, `RerankMs`, `RerankModel`. |

## API Contract

| Verb | Route | Change | Policy |
|---|---|---|---|
| GET | `/api/documents/search` | New `mode` (`vector` \| `hybrid`, default from config), `rerank` (bool, default from config), `language` of the query for the keyword config (`queryLanguage`, optional; detected when absent). Each ranked passage and candidate adds `stages`; the response adds `mode`, `rerank`, `rerankMs`. | Maintainer |
| GET | `/api/admin/chat/requests/{id}` | `retrieval` adds `mode`, `rerank`, `rerankMs`, `rerankModel`; passages add `stages`. | Maintainer |
| GET | `/api/chat/conversations/{id}/messages/{mid}/activity` | The retrieval step adds `mode`, `rerank` and per-passage `admittedBy`. | KnowledgeSeeker |
| GET | `/api/admin/chat/configuration` | Adds hybrid and reranking settings and the reranker model. | Maintainer |

Errors: `validation.failed` for an unknown `mode` or a non-boolean `rerank`. `rerank=true` while TEI is not configured → the search runs, and `rerank: skipped (unavailable)` is shown in the response; this is not an error.

The SSE answer stream is unchanged except for `retrieval.completed`, which adds `mode` and `rerank`.

## Authorization

There are no new policies. Keyword and fused pools apply the same `ReadableBy(viewerSub)` and filters (language, tags, `DocumentIds`) as the vector pool (spec 016). Overrides exist only on the maintainer endpoint. The reranker receives only passages the viewer may read.

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| Query with only stop-words or punctuation (empty tsquery) | Keyword pool is empty; hybrid equals vector. |
| French question, only English documents | No keyword hits; vector results only; `admittedBy = vector`. |
| Keyword-only hit with cosine < `MinScore`, rerank off | Not admitted. |
| Keyword-only hit, rerank on, rerank score ≥ rerank `MinScore` | Admitted. |
| TEI timeout, 5xx or connection refused | Fallback to cosine gating, `rerank: skipped (unavailable)`, a `documents.rerank.skipped` counter, a warning log without query text. |
| TEI returns a score count different from the texts sent | Treated as unavailable (fallback). |
| Document language edited from `en` to `fr` | Its passages' `search_config` becomes `french_unaccent` in the same transaction; the generated column recomputes. |
| Document language other than en/fr | `simple` config: matches exact words, no stemming. |
| Very long passages | `to_tsvector` handles them; TEI truncates input to the model's max length (512 tokens). The truncation is recorded in ADR 0010. |
| Reranking enabled but the AppHost started without TEI | The API starts; every search records `skipped (unavailable)`; the admin configuration shows reranking as enabled and unreachable. |
| Harness run with mixed modes in chat traces | Run marked invalid (as for mixed models in spec 018). |

## Non-Functional Requirements

- **Latency:**
  - the keyword pool is fetched in parallel with the query embedding, and adds ≤ 50 ms p95 at the ~50k-passage target (ADR 0006);
  - reranking 30 passages adds ≤ 800 ms median on the reference GPU machine (spec 018), CPU variant measured and reported only;
  - median time to first token stays < 3 s with every stage that is switched on.
- **Index:** the GIN index on `search_vector`; the migration on the dev corpus completes in < 1 min.
- **Observability:**
  - activities: `documents.search` tagged `mode` and `rerank`, and a child activity `documents.rerank` tagged with candidate count, ms and outcome;
  - meters: `documents.rerank.duration`, `documents.rerank.skipped`, `documents.search.keyword_hits`;
  - Chat metrics tagged `retrieval.mode`.
- **Logging:** no query or passage text above Debug.
- **Dependencies:**
  - no new NuGet package; the TEI client is a typed `HttpClient`;
  - new infrastructure: the TEI container (ADR 0010);
  - `unaccent` ships with PostgreSQL contrib in the pinned `pgvector/pgvector:0.8.7-pg18-trixie` image (to be verified at planning; if missing, the spec returns to review).

## Integrations

- **TEI** (`ghcr.io/huggingface/text-embeddings-inference`, pinned tag in ADR 0010) serving `BAAI/bge-reranker-v2-m3`, `POST /rerank` with `{ query, texts, raw_scores: false }`. Started by the AppHost only for `https-rerank`; the model cache lives in a named volume.
- **PostgreSQL:** `unaccent` extension, `french_unaccent` text search configuration, generated column and GIN index, created by the Documents migration (the MigrationService runs it).
- **Harness (spec 018):** variant runs through the search endpoint overrides; sweep of rerank thresholds.
- **Integration tests:** `IPassageReranker` replaced by a deterministic fake (scores from a lookup table); no TEI container in tests.

## Acceptance Criteria

### Hybrid

- **AC-1** Given English and French passages, when the migration runs, then each passage has `search_config` matching its document language and a non-null `search_vector`.
- **AC-2** Given an English passage containing "BX-204" whose embedding is far from the query (controlled fake vectors), when hybrid search runs for "BX-204 approval" with cosine ≥ `MinScore`, then the passage ranks higher than in vector mode and has `admittedBy = keyword` or `both`.
- **AC-3** Given the same query in vector mode, then the result equals today's vector search (spec 011 tests still pass).
- **AC-4** Given a French question "congés payés" and a French passage containing "conges payes" (no accents), when hybrid search runs with `QueryLanguage = fr`, then the passage gets a keyword rank (unaccent + stemming).
- **AC-5** Given a document whose language changes from `en` to `fr`, when the edit is saved, then its passages' `search_config` is `french_unaccent`.
- **AC-6** Given a private document of another user containing the query term, when hybrid search runs, then none of its passages appear in either pool.
- **AC-7** Given a query of only stop-words, then hybrid returns the vector result and no error.
- **AC-8** Given filters `DocumentIds`, tags and language, then the keyword pool applies the same filters.
- **AC-9** Given `k = 60` and known ranks, then the fused score equals `1/(60+rv) + 1/(60+rk)` (unit test).

### Reranking

- **AC-10** Given reranking on and a fake reranker reversing the order, then the result follows the rerank order, duplicates are reduced after reranking, and `Score` is the rerank score.
- **AC-11** Given reranking on and rerank scores all below the rerank `MinScore`, then the outcome is `NoEvidence` and the question is refused without a model call.
- **AC-12** Given a reranker that times out, then the search returns the cosine-gated result with `rerank = skipped`, the trace records it, and `documents.rerank.skipped` increments.
- **AC-13** Given `Candidates = 30` and a pool of 100, then exactly 30 texts are sent to the reranker in one call.
- **AC-14** Given a reranker returning a wrong number of scores, then it is treated as unavailable (AC-12).

### Diagnostics and API

- **AC-15** Given a maintainer, when calling the search endpoint with `mode=vector&rerank=false` and then `mode=hybrid&rerank=true`, then each response reports its mode and rerank status and per-passage `stages`, regardless of configuration.
- **AC-16** Given `mode=fuzzy`, then `400 validation.failed` for `mode`.
- **AC-17** Given an answered question, then the admin request detail shows mode, rerank status, rerank ms and per-passage stages, and the activity shows mode, rerank and `admittedBy` per passage.
- **AC-18** Given a knowledge seeker, when calling the search endpoint, then `403`.
- **AC-19** Given the `https-rerank` profile, then the AppHost starts TEI and the API reports reranking enabled; given the default profile, then no TEI resource exists.

### Evaluation gate (manual, recorded in Calibration)

- **AC-20** Given the spec 018 harness, when `run --variants` runs, then the report contains retrieval metrics for vector, hybrid and hybrid + rerank, per category and per language.
- **AC-21** Given the report, then each stage's default (`Hybrid:Enabled`, `Reranking:Enabled`) is set by the gate in Scope (≥ 3 points recall@5 or MRR, ≤ 2 points refusal drop per language, latency budget), and the decision and numbers are recorded in this spec and `appsettings.json`.
- **AC-22** Given reranking switched on by default, then the rerank `MinScore` and `ConfidentScore` come from the sweep and are recorded.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| TEI image tag, CPU vs GPU variant | Fixed in ADR 0010 at planning; GPU variant on the reference machine | Planning |
| Weighted score fusion instead of RRF | RRF only | The harness shows RRF leaves gains on the table |
| Keyword search across both language configs | Question language only | Cross-language cases show keyword misses matter |
| Reranking on CPU-only machines | Allowed, latency reported, not gated | A contributor without a GPU needs it on by default |

## Open Questions

None.
