# Spec 011: Retrieval

**Status:** Implemented (2026-10-15). Approved 2026-10-15; amended the same day during planning (the pool is re-sorted in the pipeline, not in SQL; provisional code defaults for the thresholds); thresholds calibrated (see "Calibration")
**Date:** 2026-10-15
**Roadmap:** steps 2.1–2.4 ([roadmap](../roadmap.md)) · **Builds on:** specs [002](002-shared-kernel-api-conventions.md), [006](006-documents-data-model-and-import.md), [007](007-background-ingestion-and-progress.md), [010](010-pdf-import.md) · **Decision records:** [ADR 0003](../adr/0003-model-provider.md), [ADR 0004](../adr/0004-document-versioning.md), ADR 0008 (new, HybridCache)

## Problem

Documents are indexed with embeddings, but nothing can search them yet. The Chat module (Phase 3) needs to retrieve evidence through a contract, without touching Documents internals, and that evidence must come only from eligible passages. The system must also tell "no evidence" apart from "low confidence", because the refusal path (step 3.2) and partial answers depend on it (`product.md`, Retrieval: "The system must distinguish 'no result' from 'low-confidence result'"). Maintainers need to see ranked passages, scores and the decisions made, so they can understand and tune retrieval before the inspector UI exists (Phase 4.2).

## Scope

### In

- One spec for roadmap steps 2.1–2.4.
- **2.1** The `IDocumentSearch` contract in `RagAssistant.SharedKernel/Contracts/Documents/`, the first cross-module contract. It is implemented `internal`ly in the Documents module and registered in `AddDocumentsModule`.
- **2.2** Vector search over the active, indexed version of active documents, with `language` and `tags` filters. The query embedding is cached with HybridCache.
- **2.3** Evidence rules: `topK`, two score thresholds, near-duplicate reduction, and an explicit evidence outcome.
- **2.4** The diagnostic endpoint `GET /api/documents/search`: **API only**, policy **Maintainer**.
- Supporting changes:
  - `ErrorType.Unavailable` → `503` in the shared kernel;
  - ADR 0008 for HybridCache;
  - pgvector query settings (`hnsw.iterative_scan`, `hnsw.ef_search`) applied per query.

### Out

- Any UI. The RetrievalInspector is built in Phase 4.2 on top of the diagnostic endpoint.
- Searching history (superseded versions, archived documents). See Deferred Decisions.
- Filters on owner, effective date and document IDs.
- Hybrid or full-text search, and reranking (roadmap "Later").
- Rewriting follow-up queries (step 3.5), and anything in the Chat module.
- Caching retrieval results.
- Timings in the diagnostic response (they are in the trace).
- Any schema change or migration.

## Domain Model

No new entity is persisted. Retrieval reads `documents`, `document_versions` and `passages` (existing HNSW index `vector_cosine_ops` on `passages.embedding`).

### Eligibility

A passage can be retrieved only when **all** of these hold:

1. its document's `Status` is `Active` (archived documents are never searched);
2. its version is the document's `ActiveVersionId`, which by construction is `Indexed` (ADR 0004);
3. that version's `EmbeddingModel` equals the model the generator reports now (`PassageEmbedder.ModelId`). Versions embedded by another model would produce meaningless scores, so they are excluded and counted as `skippedForModelMismatch` (they need reprocessing);
4. it matches the filters.

### Filters (both optional)

| Filter | Rule (same as the document list, spec 008) |
|---|---|
| `Language` | Two ASCII letters, case-insensitive, exact match on the document language |
| `Tags` | The document must have **all** the given tags, normalized like import (trimmed, lowercased); ≤ 20 |

### Query

- Trimmed. 1–1,000 characters; otherwise validation fails (`query` field).
- **Normalized for the cache key:** trimmed and whitespace runs collapsed to one space. Case is kept.

### Pipeline

1. **Validate** the query, `topK` (`1..MaxTopK`) and the filters.
2. **Embed the query** through `IEmbeddingGenerator`, behind HybridCache.
   - The cache key is the embedding model plus the normalized query, and entries expire after `QueryEmbeddingCacheDuration`.
   - A cache miss makes one call bounded by `QueryEmbeddingTimeout`. Any failure other than the caller's cancellation gives `retrieval.embedding_unavailable`.
3. **Fetch the candidate pool** of `min(topK × CandidateMultiplier, MaxCandidates)` eligible passages, ordered by cosine distance.
   - It runs in a transaction with `SET LOCAL hnsw.iterative_scan = relaxed_order` and `SET LOCAL hnsw.ef_search = max(40, pool size)`.
   - The SQL orders by distance alone, because pgvector uses the HNSW index only for that `ORDER BY`. The pipeline then re-sorts the pool by score, document ID and position, which also corrects the slight disorder that relaxed order allows. Filtered queries keep scanning until the pool fills or the index is exhausted.
   - The query uses `CosineDistance` + `OrderBy` + `Take`; embeddings are never loaded into memory to compute similarity.
4. **Score** each passage: `score = 1 − cosine distance`, so higher is better.
5. **Apply the floor:** passages with `score < MinScore` are discarded (`below_min_score`).
6. **Reduce near-duplicates.** Walk the survivors in score order. A passage is discarded (`duplicate`) when the Jaccard similarity of its word 3-grams with an already kept passage is `≥ DuplicateOverlap`; the duplicate records which passage it duplicates and the overlap. Words are lowercased, and split on whitespace and punctuation. A passage with fewer than 3 words is compared as a single shingle.
7. **Cut** to `topK`.
8. **Decide the outcome:**

   | Outcome | Rule | Meaning for Chat (Phase 3) |
   |---|---|---|
   | `NoEvidence` | No passage survives the floor (includes an empty or fully filtered corpus) | Refuse |
   | `LowConfidence` | The best kept score `< ConfidentScore` | Partial answer, with a warning |
   | `Sufficient` | The best kept score `≥ ConfidentScore` | Answer |

**Ordering:** score descending, then document ID, then passage position. Equal scores therefore always come out in the same order.

### Options — `Documents:Retrieval` (`RetrievalOptions`, validated on start)

| Option | Default | Rule |
|---|---|---|
| `DefaultTopK` | 8 | `1..MaxTopK` |
| `MaxTopK` | 20 | ≥ 1 |
| `CandidateMultiplier` | 4 | ≥ 1 |
| `MaxCandidates` | 200 | ≥ `MaxTopK` |
| `MinScore` | calibrated (see below); provisional code default 0.5 | `0..1` |
| `ConfidentScore` | calibrated; provisional code default 0.6 | `MinScore..1` |
| `DuplicateOverlap` | calibrated; provisional code default 0.8 | `0 < x ≤ 1` |
| `QueryEmbeddingTimeout` | 10 s | > 0 |
| `QueryEmbeddingCacheDuration` | 1 h | > 0 |

**Threshold calibration.** During implementation, a short spike runs a handful of on-corpus questions (whose answers are in the demo documents) and off-corpus questions against `bge-m3`. The `MinScore`, `ConfidentScore` and `DuplicateOverlap` values it picks go into `appsettings.json`. This spec records them in a "Calibration" note together with the questions and the observed score ranges. They stay **provisional** until Phase 5 measures them.

### Calibration (2026-10-15, provisional until Phase 5)

There is no demo corpus yet (Phase 5.1), so five short English Markdown policies were imported into the dev database with the tag `calibration`: Budget Exception Policy, Travel and Expense Policy, Remote Work Policy, Information Security Incident Response and New Employee Onboarding Guide. They are two passages each, and the travel policy repeats the budget policy's "Approval thresholds" paragraph. The questions ran through `GET /api/documents/search` with `tags=calibration` against Ollama `bge-m3`.

| Question set | Best score observed |
|---|---|
| 8 on-corpus questions (approval of a 7,000-euro exception, unanswered approver, meal allowance abroad, expense deadline, home-office days, working abroad, clicked phishing link, manager probation) | 0.574 to 0.721; the expected document ranked first every time. The best passage of another document reached 0.583 |
| 6 off-corpus questions (capital of Australia, sourdough bread, first programming language, parental leave policy, company car, canteen hours) | 0.310 to 0.528; the in-domain but absent "parental leave" was highest |

Overlap between passages, measured with the same 3-gram Jaccard:
- passages that share the copied paragraph but otherwise differ: 0.49;
- neighbouring passages of the same document: below 0.02.

Chosen values, in `src/RagAssistant.Api/appsettings.json`:

- **`MinScore` 0.55.** All on-corpus evidence (≥ 0.574) passes, and every off-corpus question gives `NoEvidence`.
- **`ConfidentScore` 0.60.** 7 of the 8 on-corpus questions are `Sufficient`. The phishing question (0.574) is `LowConfidence`.
- **`DuplicateOverlap` 0.8.** Only near-identical passages are dropped. Passages that share a paragraph but carry different facts (0.49) are both kept.

The margins are small: 0.528 against 0.574 at the floor. Phase 5 must re-measure them on the golden dataset.

## API Contract

### Contract (`RagAssistant.SharedKernel/Contracts/Documents/`, namespace `RagAssistant.SharedKernel.Contracts.Documents`)

```csharp
public interface IDocumentSearch
{
    /// <summary>Ranked evidence for a question. Never throws for an unavailable model: returns retrieval.embedding_unavailable.</summary>
    Task<Result<SearchResult>> SearchAsync(string query, SearchFilters filters, int topK, CancellationToken cancellationToken);
}

public sealed record SearchFilters(string? Language = null, IReadOnlyList<string>? Tags = null)
{
    public static SearchFilters None { get; } = new();
}

public enum EvidenceOutcome { NoEvidence, LowConfidence, Sufficient }

public sealed record EvidenceThresholds(double MinScore, double ConfidentScore, double DuplicateOverlap);

public sealed record SearchResult(
    EvidenceOutcome Outcome,
    IReadOnlyList<RetrievedChunk> Chunks,       // kept passages, ranked; empty when NoEvidence
    EvidenceThresholds Thresholds,              // the values applied, for Chat's request trace (4.1)
    string EmbeddingModel);

public sealed record RetrievedChunk(
    Guid PassageId,
    Guid DocumentId,
    string DocumentTitle,
    string Language,
    Guid VersionId,
    int VersionNumber,
    IReadOnlyList<string> SectionPath,
    int Position,
    int? PageStart,
    int? PageEnd,
    string Text,                                // full passage text (≤ ~400 tokens)
    double Score);                              // cosine similarity, higher is better
```

- **Document status** (listed in the roadmap) is left out of `RetrievedChunk`. Only active documents are eligible, so it would always be `Active`. History search (deferred) would add it.
- **Diagnostics stay out of the contract.** The discarded passages and the mismatch count are internal to Documents and appear only on the endpoint.
- **Failures:** `validation.failed` with field errors (`query`, `topK`, `language`, `tags`), or `retrieval.embedding_unavailable` (`ErrorType.Unavailable`).

### Endpoint

| Verb | Route | Query parameters | Success | Errors | Policy |
|---|---|---|---|---|---|
| GET | `/api/documents/search` | `q` (required), `language`, `tags` (repeated), `topK` (optional, default `DefaultTopK`) | `200 SearchDiagnostics` | 400 `validation.failed` · 401 · 403 · 503 `retrieval.embedding_unavailable` | `Maintainer` |

- The route must not clash with `/api/documents/{id:guid}`; a test covers it.
- The endpoint and `IDocumentSearch` share one internal pipeline. The contract maps the pipeline's result down and drops the diagnostics.
- The ASP.NET Core OpenTelemetry instrumentation redacts query-string values by default; keep it that way.

```jsonc
{
  "query": "approval threshold for a budget exception",
  "outcome": "sufficient",                       // noEvidence | lowConfidence | sufficient
  "topK": 8,
  "embeddingModel": "bge-m3",
  "thresholds": { "minScore": 0.5, "confidentScore": 0.62, "duplicateOverlap": 0.7 },
  "skippedForModelMismatch": 0,                  // active documents matching the filters whose active version used another model
  "passages": [
    { "rank": 1, "passageId": "…", "documentId": "…", "documentTitle": "Budget Exception Policy", "language": "en",
      "versionNumber": 3, "sectionPath": ["Approval", "Thresholds"], "position": 12,
      "pageStart": 4, "pageEnd": 4, "text": "…", "score": 0.7134 }
  ],
  "discarded": [
    { "passageId": "…", "documentId": "…", "documentTitle": "…", "versionNumber": 3, "position": 13, "score": 0.6990,
      "reason": "duplicate", "duplicateOf": "…", "overlap": 0.81 },
    { "passageId": "…", "documentId": "…", "documentTitle": "…", "versionNumber": 1, "position": 2, "score": 0.4120,
      "reason": "below_min_score" }
  ]
}
```

- Scores are rounded to 4 decimals on the endpoint only. The contract keeps full precision.
- `discarded` lists only the candidates dropped by the floor or as duplicates. Candidates beyond `topK` after the cut are not listed. Discarded entries carry no text.

## Authorization

- `GET /api/documents/search`: `Maintainer` only. Scores and threshold decisions are tuning information.
- `IDocumentSearch` does no authorization of its own. Every authenticated caller of Chat may search the whole eligible corpus; per-corpus access rules are "Later" (ADR 0002).

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| Embedding model unreachable, not pulled, or timing out | `retrieval.embedding_unavailable` (`503` on the endpoint). Chat (3.3) maps it to an `error` event with `retryable: true`. **Never** reported as `NoEvidence`. Nothing is cached. |
| Caller cancels | `OperationCanceledException` propagates (normal cancellation); not mapped to an error |
| Empty corpus, or filters match nothing | `NoEvidence`, empty `passages`; not an error |
| A version embedded by another model | Excluded; counted in `skippedForModelMismatch` |
| Document archived, or a new version activated, between two searches | The next search reflects it immediately (results are never cached) |
| Ingestion activates a version during a search | The search sees one consistent snapshot (the statement runs in a single transaction) |
| Fewer eligible passages than the pool | The pool is smaller and the result has fewer than `topK` passages; not an error |
| Same text in two documents, or chunk overlap between neighbours | One kept, the others listed as `duplicate` |
| Generator returns a vector of the wrong length | `retrieval.embedding_unavailable` with a Warning log (a misconfigured model counts as unavailable) |
| Invalid `q`, `topK`, `language` or `tags` | `400 validation.failed` with an error per field |
| Same query from many users at once | HybridCache stampede protection makes one embedding call |

## Non-Functional Requirements

- **Volume:** about 100 documents and about 50k passages (ADR 0004, amended by ADR 0006). The default HNSW build parameters stay.
- **Latency targets** (targets, not tests; steps 4.3 and Phase 5 measure them):
  - the vector query (candidate pool and filters) targets p95 < 300 ms;
  - the query-embedding call is bounded by `QueryEmbeddingTimeout` (10 s), so a hung model fails fast. Chat's time-to-first-token budget is < 3 s (ADR 0004).
- **Observability:**
  - Activity `documents.search`, tagged with `topK`, `filter.language` and `filter.tags` (presence only), candidate count, returned count, outcome, best score and `embedding.cache_hit`.
  - `DocumentsMetrics` gains `documents.search.duration` (a histogram, seconds) and `documents.search.outcomes` (a counter by outcome; failures are tagged by error code).
  - The query text is logged only at Debug. Passage text is never logged.
- **Caching:** in-memory HybridCache, no L2 (ADR 0008). Only query embeddings are cached; results never are.

## Integrations

- **Embedding provider:** `IEmbeddingGenerator<string, Embedding<float>>` as wired in the API (Ollama `bge-m3`, ADR 0003). Demo mode does not affect retrieval: embeddings are always real.
- **Chat module (Phase 3):** consumes `IDocumentSearch` only. Nothing is published and there are no events.
- **Shared kernel:**
  - `Contracts/Documents/` (new folder; plain interfaces and records, no logic);
  - `ErrorType.Unavailable` mapped to `503` in `ResultHttpExtensions`.
- **Packages:** `Microsoft.Extensions.Caching.Hybrid` in `Directory.Packages.props`, justified by ADR 0008. It is registered by the API (`AddHybridCache`) and used by the Documents module.
- **Tests:** the integration tests' `FakeEmbeddingGenerator` gains a scripted map from keyword to fixed unit vector (or a blend), so passage and query scores are exact. Its existing `ReportedModel` drives the model-mismatch case, and `UnavailableEmbeddingGenerator` (or `Available = false`) drives the 503 case.
- **Docs:**
  - roadmap Phase 2 marked done, with the `Shared/Contracts` path corrected to `SharedKernel/Contracts`;
  - CLAUDE.md module table and stack line;
  - the `rag-pipeline` skill is still created in 3.3, as planned.

## Acceptance Criteria

Tests resolve `IDocumentSearch` from `RagAssistantAppFactory` (contract) or call the endpoint as a persona (endpoint), with controlled vectors. The near-duplicate and outcome rules are also unit-tested in the Documents module tests.

**Eligibility and ranking (contract)**

- **AC-1** Given three indexed documents whose passages have known similarities to the query, when searching with `topK = 2`, then the two passages with the highest scores are returned in descending score order, with `score = 1 − cosine distance` and all `RetrievedChunk` fields filled from the document, the active version and the passage (including page numbers for a PDF passage).
- **AC-2** Given a document with version 1 superseded by an indexed version 2, when searching for text that matches only version 1, then no version 1 passage is returned.
- **AC-3** Given an archived document whose passages match best, when searching, then none of its passages is returned.
- **AC-4** Given a document whose only version is queued, processing or failed, when searching, then none of its passages is returned.
- **AC-5** Given a document whose active version's `EmbeddingModel` differs from the generator's reported model, when searching, then its passages are excluded, and the endpoint reports `skippedForModelMismatch = 1`.
- **AC-6** Given equal scores, when searching twice, then the order is identical (document ID, then position).

**Filters**

- **AC-7** Given documents in `en` and `fr`, when searching with `Language = "FR"`, then only `fr` passages are returned.
- **AC-8** Given documents tagged `[budget, finance]` and `[budget]`, when searching with `Tags = ["Budget ", "finance"]`, then only passages of the first document are returned.
- **AC-9** Given a filter that matches few passages, all of them scoring below many unfiltered passages, when searching with `topK = 5`, then the matching passages are still returned (up to 5); the iterative scan does not stop at the unfiltered neighbours.

**Evidence rules**

- **AC-10** Given candidates scoring below and above `MinScore`, when searching, then only those `≥ MinScore` are returned, and the endpoint lists the others as `below_min_score` with their scores.
- **AC-11** Given no candidate `≥ MinScore` (including an empty corpus), when searching, then `Outcome = NoEvidence` with no chunks, and no error.
- **AC-12** Given a best kept score between `MinScore` and `ConfidentScore`, then `Outcome = LowConfidence`; given a best score `≥ ConfidentScore`, then `Outcome = Sufficient`.
- **AC-13** Given two passages (same or different documents) whose word 3-gram Jaccard is `≥ DuplicateOverlap`, when both rank in the pool, then only the higher-scoring one is returned, and the endpoint lists the other as `duplicate` with `duplicateOf` and `overlap`; given an overlap below the ratio, then both are returned.
- **AC-14** Given duplicates removed from the top results and more eligible candidates in the pool, then the result is refilled up to `topK` from the pool.
- **AC-15** Given `topK` of 0, `MaxTopK + 1`, an empty or whitespace query, a 1,001-character query, a `language` that is not two letters, or 21 tags, then the contract returns `validation.failed` with an error for that field, and the endpoint returns `400`.
- **AC-16** Given `SearchResult.Thresholds` and `EmbeddingModel`, then they equal the configured values and the reported model.

**Caching and failures**

- **AC-17** Given the same query twice (differing only in surrounding and repeated whitespace), then the embedding generator is called once; given a query differing in case, then it is called again.
- **AC-18** Given a document archived after a first search, when the same query runs again, then its passages are gone (results are not cached).
- **AC-19** Given the embedding generator is unavailable or times out, then the contract returns `retrieval.embedding_unavailable` (`ErrorType.Unavailable`), the endpoint returns `503` ProblemDetails with that code, and a later search with the generator restored succeeds (the failure was not cached).
- **AC-20** Given the caller cancels, then the search stops with `OperationCanceledException`.

**Endpoint**

- **AC-21** Given no token → `401`; Knowledge seeker or Content owner → `403`; Maintainer → `200` with the `SearchDiagnostics` shape above (outcome in camelCase, scores rounded to 4 decimals, discarded entries without text).
- **AC-22** Given `GET /api/documents/search` and `GET /api/documents/{id}`, then both routes resolve to their own endpoints (no route clash).
- **AC-23** Given `topK` omitted, then `DefaultTopK` is used and reported.

**Configuration and telemetry**

- **AC-24** Given `ConfidentScore < MinScore`, `MaxCandidates < MaxTopK` or a non-positive duration, then the API fails to start with an options validation error.
- **AC-25** Given a search, then an activity `documents.search` is recorded with the outcome and counts, and the outcome counter is incremented; the query text does not appear in any log above Debug.

## Deferred Decisions

| Decision | Fallback now | Revisit when |
|---|---|---|
| History search (ADR 0004 "unless history is explicitly requested") | Only the active, indexed version of active documents is searchable, both in the contract and on the endpoint | A real caller needs history, e.g. the Phase 4 inspector or version UX in Phase 6 |
| Threshold values | Calibrated by a spike during implementation and marked provisional | Phase 5 evaluation (recall@k, refusal correctness) |
| Owner, effective-date and document-ID filters | Not supported | Chat needs "ask about this document", or the inspector needs them |
| Retrieval latency enforcement | Targets only, no test | Phase 4.3 metrics show a problem |
| Per-document cap on results | None; only text-overlap reduction | Evaluation shows one long document crowding out others |

## Open Questions

None.
