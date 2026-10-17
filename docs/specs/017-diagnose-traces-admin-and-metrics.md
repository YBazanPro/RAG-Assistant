# Spec 017: Diagnose — request traces, admin area and metrics

**Status:** Approved (2026-10-16)
**Date:** 2026-10-16
**Roadmap:** Phase 4 (steps 4.1, 4.2, 4.3). Builds on specs 007, 011, 012, 014, 015 and 016.

## Problem

A maintainer cannot yet answer "why did the assistant answer (or refuse) this?" (product.md, Journey 4). Spec 012 writes one `chat.request_traces` row per attempt, but nothing reads another user's trace: `/admin` is an empty stub. Ingestion failures are visible only on each document's own page, and only to readers of the document. Ratings and issue reports (spec 014) are stored but unseen. Chat has no metrics at all, and the maintainers' view of private documents was deferred to this spec (spec 016).

This spec makes the assistant diagnosable by maintainers: read traces, see what is failing, see the health of the corpus, see the effective configuration, and watch a few practical numbers, without weakening the privacy rules of ADR 0004 and ADR 0009.

## Scope

### In

- **Traces, read side (4.1):** maintainer-only endpoints that list and read request traces of all users.
- **Admin area (4.2)**, tabs on `/admin`:
  - **Overview:** corpus health plus a summary of recent chat behaviour (4.3).
  - **Requests:** paged list with filters, and a request detail side panel with the `RetrievalInspector`.
  - **Ingestion failures:** currently failed versions with a retry for shared documents.
  - **Feedback:** negative ratings and issue reports, each linked to its request.
  - **Private documents:** metadata-only list (title, owner, status), the read path spec 016 deferred.
  - **Configuration:** read-only, non-secret options of Documents and Chat.
- **Metrics (4.3):** OpenTelemetry meters in the Chat module (retrieval results, refusals, time to first token, errors per stage), and a summary of the same facts in `/admin`, computed from stored traces.
- A configurable link from a request to its Aspire trace.
- A maintainer-only Documents contract that tells the Chat module which document IDs are currently shared, with their titles.

### Out

- Evaluation results and anything from Phase 5.
- Creating an issue or an evaluation case from a request (Journey 4, step 5): Phase 5 defines the case format. The inspector offers nothing for it.
- Reading answer text, passage text or whole conversations of other users. A maintainer sees the trace only.
- An admin audit trail and a configuration editor. The only admin write is the existing ingestion retry, which is logged by the existing request logging.
- Editing or deleting traces, ratings or reports from the admin area. Retention stays as in ADR 0004 D4 (indefinite, deleted with the owner's conversation).
- Retrying ingestion of a private document by a maintainer (no bypass, ADR 0009).
- Live updates: no polling and no SSE in the admin area.
- Filtering requests by owner, by date range or by evidence outcome.
- User management (users and roles stay in the realm file).

## Domain Model

No new tables and no schema changes. Everything is read from existing data.

| Source | Used for |
|---|---|
| `chat.request_traces` (spec 012, 014, 015) | request list, request detail, summary |
| `chat.message_ratings`, `chat.issue_reports` (spec 014) | feedback list and the feedback shown on a request |
| `documents.ingestion_runs`, `document_versions`, `documents` | failures, corpus health, private-document list |

### Request (the unit of the list)

One trace row is one **request** (one attempt). Its identity is the trace `Id`. A retried answer therefore appears as several requests with `Attempt` 1, 2, ….

### Trace in the inspector

The detail shows, in this order, for the trace of one attempt:

1. **Question:** `QuestionText`, the filters (`FilterLanguage`, `FilterTags`, `FilterDocumentIds`, `UnavailableDocumentIds`), the owner (`OwnerSub`, shortened with a copy button), `StartedAt`, `Attempt`.
2. **Rewrite:** whether it was used, `RetrievalQuery`, `RewriteFallbackReason`, `RewriteMs`, rewrite tokens, `RewritePromptVersion`.
3. **Retrieval:** `EvidenceOutcome`, the thresholds (`MinScore`, `ConfidentScore`, `DuplicateOverlap`), `BestScore`, `EmbeddingModel`, `RetrievalMs`, and the kept passages: rank, score, passage ID, version number, section path, pages, marker, cited.
4. **Generation:** `ChatModel`, `PromptVersion`, tokens, `SentinelHit`, `StrippedMarkerCount`, `FirstTokenMs`, `TotalMs`.
5. **Result:** `FinalStatus`, `RefusalReason`, `ErrorCode`.
6. **Feedback** on the answer: rating and reports (category and comment).
7. **Trace link:** `OtelTraceId`, copyable, and a link when `Admin:TraceUrlTemplate` is set.

The inspector shows **no answer text and no passage text**. Document titles come from the lookup below, never from the title stored in the trace.

### Title lookup (privacy)

Traces store a document title for every kept passage (spec 015), including private documents. The inspector must not show a private title to a maintainer, so it asks the Documents module which of the trace's document IDs are **currently shared** and shows the current title of those. Any other ID (private, archived-and-private, deleted, or a trace written before spec 015 without IDs) shows as "Private or unavailable document". Archived shared documents are shown with their title.

New contract in `RagAssistant.SharedKernel/Contracts/Documents/`: `IDocumentTitles.GetSharedTitlesAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken)` returning `IReadOnlyDictionary<Guid, string>` for shared documents only. It takes no viewer: it can only ever reveal what every signed-in user can already read, and is called only by Maintainer endpoints. It returns no private-document information, not even whether an ID exists.

### Stale document

An **active shared** document is stale when the `CreatedAt` of its latest version is older than `Documents:Admin:StaleAfterDays` (default 180). Private documents are never counted as stale.

## API Contract

All routes are under `/api/admin`, policy `Maintainer`. Enums are lowercase strings (spec 012). Lists use the existing paged-result shape (`items`, `page`, `pageSize`, `totalCount`); `pageSize` default 25, maximum 100. Newest first.

### Chat module

| Verb | Route | Purpose |
|---|---|---|
| GET | `/api/admin/chat/requests` | Paged list of requests |
| GET | `/api/admin/chat/requests/{id}` | Request detail (inspector) |
| GET | `/api/admin/chat/summary?window=24h\|7d\|30d` | Aggregates over the window (default `7d`) |
| GET | `/api/admin/chat/feedback` | Paged list of negative ratings and issue reports |
| GET | `/api/admin/chat/configuration` | Effective non-secret Chat options |

**List filters:** `status` (`complete`, `partial`, `refused`, `failed`, `cancelled`, optional, repeatable) and `feedback=negative` (only requests whose answer is rated not helpful or has at least one issue report). Unknown values are `400 validation.failed`. Items carry: `id`, `startedAt`, `ownerSub`, `questionText` (cut at 120 characters), `finalStatus`, `refusalReason`, `errorCode`, `evidenceOutcome`, `bestScore`, `totalMs`, `attempt`, `hasNegativeFeedback`.

**Detail:** the fields of "Trace in the inspector" above, with passages carrying `documentTitle` only when the document is currently shared, and `feedback: { rating?, reports: [{ category, comment, createdAt }] }`. The conversation ID and message IDs are included for support but there is no link into the conversation.

**Summary** (computed from traces whose `StartedAt` is inside the window, at the time of the call):

- `requests`, and counts per `finalStatus`;
- `refusalRate` (refused ÷ requests, 0 when none);
- counts per `evidenceOutcome`;
- `firstTokenMs` p50 and p95 over requests that streamed a first token (null when none);
- `totalMs` p50 and p95;
- `errors`: counts per `errorCode`, each with its stage (see Metrics);
- `negativeFeedback`: requests with negative feedback.

**Feedback list:** one row per negative rating or issue report, newest first: `kind` (`rating` | `report`), `category?`, `comment?`, `createdAt`, `requestId` (the latest trace of that answer), `questionText` (cut at 120 characters), `ownerSub`.

**Configuration:** `chatModel`, `provider` mode (`Ollama` | `Demo`), `promptVersion`, `rewritePromptVersion`, retrieval `topK`, history window, answer and question limits, timeouts, `MaxOutputTokens`. Only values from the Chat options classes; never connection strings, keys or URLs with credentials.

### Documents module

| Verb | Route | Purpose |
|---|---|---|
| GET | `/api/admin/documents/health` | Corpus health counts |
| GET | `/api/admin/documents/failures` | Paged list of currently failed versions |
| GET | `/api/admin/documents/private` | Paged metadata-only list of private documents |
| GET | `/api/admin/documents/configuration` | Effective non-secret Documents options |

**Health:** counts of documents by status (`active`, `archived`) split by visibility (`shared`, `private`); versions by status (`queued`, `processing`, `indexed`, `failed`, `superseded`); `failedVersions`; `queuedVersions` and the age of the oldest queued or processing run; `staleShared` (see Stale document) and `staleAfterDays`. It reports nothing that identifies a private document.

**Failures:** one row per version whose **latest run** is `failed`: `documentId`, `versionId`, `versionNumber`, `title`, `visibility`, `ownerSub`, `stage` (`currentStage`), `errorCode`, `errorMessage`, `failedAt`, `attempt`, `canRetry`. For a **shared** document: the title, `canRetry` true. For a **private** document: the same row without retry (`canRetry` false) from the metadata-only path. Retry uses the existing `POST /api/documents/{id}/ingestion/retry`; this spec adds no retry endpoint. For a private document that call is 404 for a maintainer, so the UI shows no button.

**Private documents:** `documentId`, `title`, `ownerSub`, `status`, `latestVersionStatus`, `createdAt`, `updatedAt`. No tags, no description, no passage, no link into the document. Implemented on its own query inside the Documents module; it does not use `ReadableBy` and no other read path is changed.

**Configuration:** embedding model name and dimensions, chunk size and overlap, ingestion timeouts and retry counts, search defaults (`MinScore`, `ConfidentScore`, `DuplicateOverlap`, `topK` cap), upload limits, storage kind (not the path), `StaleAfterDays`.

### Web (BFF)

No BFF change: `/api/**` is forwarded as is. The configuration tab calls both configuration endpoints. The only setting added to the Web side is the trace link: `Admin:TraceUrlTemplate` (for example `https://localhost:17000/traces/detail/{traceId}`) is read by the **API** and returned in the request detail as `traceUrl` (null when unset), so the browser project needs no configuration. The AppHost sets it to the Aspire dashboard.

### Errors

| Case | Response |
|---|---|
| Not signed in | 401 |
| Signed in below `maintainer` | 403 |
| Unknown request `id` | 404 `chat.request_not_found` |
| Invalid `status`, `window`, `page` or `pageSize` | 400 `validation.failed` |

## Authorization

Every admin endpoint is `Maintainer` only (`knowledge-seeker` and `content-owner` get 403). The `/admin` page keeps `[Authorize(Policy = PolicyNames.Maintainer)]` and the navigation entry stays visible only to maintainers.

- Maintainers see the **trace** of any user's request, not the answer or conversation. The owner is the `sub`.
- Maintainers have no bypass for private documents. They get metadata through the dedicated list and see no private titles inside traces. Spec 016's "Maintainers have no bypass" rule is kept, and its Deferred Decision about a maintainer metadata view is closed by this spec.
- The ingestion retry button follows the existing `ContentOwner` policy (a maintainer includes it) and the existing readability rule.

## Metrics

A new meter in the Chat module, named `RagAssistant.Chat` (already covered by `AddMeter("RagAssistant.*")`), in the same style as `DocumentsMetrics`.

| Instrument | Type | Labels |
|---|---|---|
| `ragassistant.chat.requests` | counter | `status`, `evidence_outcome` |
| `ragassistant.chat.refusals` | counter | `reason` (`no_evidence`, `model_declined`) |
| `ragassistant.chat.retrieval.results` | histogram (kept passages per request) | `evidence_outcome` |
| `ragassistant.chat.first_token.duration` | histogram, seconds | none |
| `ragassistant.chat.request.duration` | histogram, seconds | `status` |
| `ragassistant.chat.errors` | counter | `stage` (`rewrite`, `retrieval`, `generation`, `persistence`), `error_code` |

Rules:

- Labels are low-cardinality only: never user, conversation, document or passage IDs, and never question text.
- A metric is recorded from the same place and values as the trace, once per attempt.
- Documents keeps its existing meters (ingestion durations, search duration and outcomes).
- The `errors` summary in `/admin` maps each stored `ErrorCode` to a stage with one shared mapping, used by both the metric and the summary: `retrieval.*` → `retrieval`; `chat.model_*` → `generation`; `chat.internal` and `chat.interrupted` → `persistence`.
- The `rewrite` stage has no `ErrorCode`, because a failed rewrite falls back to the original question (spec 014). It is counted from `RewriteFallbackReason`, as `error_code` `rewrite.timeout`, `rewrite.model_error` or `rewrite.invalid`, both in the `errors` metric and in the summary's `errors`. A request can therefore count one rewrite error and still finish `complete`. *(Amended 2026-10-16 during planning, agreed by the developer.)*

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| No requests in the window | Summary returns zeros, `refusalRate` 0, percentiles null; the UI shows an empty state |
| Trace written before spec 014 or 015 (missing rewrite, document fields) | Fields are null; the inspector shows "Not recorded" and the passage's document as "Private or unavailable document" |
| Owner deleted the conversation | Its traces and feedback are gone (spec 014); an open detail returns 404 and the UI says the request no longer exists |
| Document of a trace was deleted or became private | Shows as "Private or unavailable document" |
| Document changed from shared to private after the answer | The title is hidden from then on (current visibility decides) |
| Retry from the failures list races with another retry | The existing endpoint's conflict response is shown; the list refresh then drops the row |
| Failure row whose document is private | No retry button; text "Only the owner can retry" |
| Documents lookup fails during request detail | The detail still loads; titles show as unavailable and a notice says titles could not be loaded |
| Very large `RetrievedPassages` | Bounded by `topK`; no paging inside the detail |
| `pageSize` over 100 | 400 |
| Feedback on an answer with several attempts | Feedback belongs to the answer; it is shown on every attempt of it, and the feedback list links to the latest attempt |
| Percentiles over few rows | Computed on whatever exists; one row gives p50 = p95 |
| Admin tab data fails to load | That tab shows `ErrorState` with a retry; other tabs are unaffected |

## Non-Functional Requirements

- **Volume:** about 500k traces a year (spec 012). List queries use the existing indexes on `started_at`; the `feedback=negative` and `status` filters may scan within the window and are acceptable at this scale. Add an index only if a query plan shows a need, with a migration.
- **Latency target:** lists and detail respond in under 1 s; the summary in under 2 s for a 30-day window at the stated volume. These are targets and are not asserted in tests.
- **Summary computation:** done in SQL (counts, `percentile_cont`), never by loading rows into memory.
- **Privacy:** question text is the only free text of other users shown; the report comment is shown on the request and in the feedback list. Logs never contain question text above Debug (existing rule).
- **Accessibility:** tabs, tables and the side panel are keyboard-operable with visible focus and labels; the side panel closes on Escape and returns focus to the row that opened it.
- **English only**, plain text, as everywhere.

## Integrations

- Chat → Documents through the new `IDocumentTitles` contract, implemented `internal` in Documents and registered in `AddDocumentsModule`; no project reference between modules.
- Aspire dashboard: link only, through `Admin:TraceUrlTemplate`.
- OpenTelemetry: the new Chat meter, exported through the existing ServiceDefaults.
- No external services, no messages, no webhooks.

## Acceptance Criteria

**Authorization**

- **AC-1** Given the six admin routes of Chat and Documents (except configuration), when called with no token, then 401; as `knowledge-seeker` or `content-owner`, then 403; as `maintainer`, then 200. (Data-driven over all `/api/admin/**` routes, configuration included.)
- **AC-2** Given a signed-in `maintainer`, when `/admin` is opened, then the tabs render; given a `content-owner`, then the page is access-denied and the navigation has no Admin entry.

**Requests (4.1)**

- **AC-3** Given traces of two different owners, when a maintainer lists requests, then both appear newest first, with the owner `sub`, the question cut at 120 characters and no answer text.
- **AC-4** Given traces with different statuses, when filtered by `status=refused&status=failed`, then only those appear; `feedback=negative` returns only requests whose answer is rated not helpful or has a report; an unknown `status` is 400.
- **AC-5** Given a trace of an answer that used a rewrite, when the detail is read, then it returns `retrievalQuery`, the fallback reason, the thresholds, `bestScore`, the model names, prompt versions, tokens, timings and the result, and contains no passage text and no answer text.
- **AC-6** Given a trace whose kept passages belong to a shared and a private document, when the detail is read, then the shared passage carries its current title and the private one carries no title (`documentTitle` null), whatever title the trace stored.
- **AC-7** Given a document that was shared when answered and is now private (or deleted), when the detail is read, then its passage shows no title.
- **AC-8** Given an answer with a rating and two reports, when the detail of any of its attempts is read, then the feedback appears; the feedback list has one row per negative rating and per report, each linking to the latest attempt.
- **AC-9** Given `Admin:TraceUrlTemplate` set, then `traceUrl` is the template with the trace ID; given it unset, then `traceUrl` is null and the UI shows only the ID.
- **AC-10** Given an unknown request ID, then 404; given a conversation deleted by its owner, then its requests are gone from the list.
- **AC-11** Given a trace written without the spec 015 document fields, when the detail is read, then it loads, with the missing fields null.

**Summary and metrics (4.3)**

- **AC-12** Given traces inside and outside a 24 h window, when the summary is requested with `window=24h`, then only the inside ones are counted; `refusalRate`, counts per status and outcome, p50/p95 of `firstTokenMs` and `totalMs`, and the errors per stage and code match the rows.
- **AC-13** Given no traces in the window, then zeros and null percentiles; an invalid `window` is 400.
- **AC-14** Given a refused, a complete and a failed (retrieval) request run through the pipeline with fakes, when the `RagAssistant.Chat` meter is observed, then `requests`, `refusals` and `errors` (with `stage` `retrieval`) record one measurement each with the labels above, and no label holds an ID or text.
- **AC-15** Given a streamed answer with a first token, then `first_token.duration` records once; given a refusal, then it records nothing.

**Corpus health and failures (4.2)**

- **AC-16** Given documents of both visibilities and statuses and versions in several states, when health is read, then the counts match, `staleShared` counts only active shared documents whose latest version is older than `StaleAfterDays`, and no private document is identifiable.
- **AC-17** Given a failed latest run on a shared version, when failures are listed, then the row has the stage, error code, message and `canRetry` true; after a successful retry the row is gone.
- **AC-18** Given a failed latest run on a private version, then the row shows title, owner, stage and error code with `canRetry` false; and the existing retry endpoint returns 404 for a maintainer who is not the owner.
- **AC-19** Given a version that failed once and then succeeded, then it is not listed.

**Private documents (spec 016 follow-up)**

- **AC-20** Given private documents of two owners, when the maintainer lists them, then each row has `documentId`, `title`, `ownerSub`, `status`, `latestVersionStatus`, dates and nothing else (no tags, no description, no passage); the ordinary list, search and detail endpoints still return 404 or omit them for the maintainer.
- **AC-21** Given the Documents module, then `IDocumentTitles.GetSharedTitlesAsync` returns titles for shared IDs only, and nothing (not even an empty entry that reveals existence) for private or unknown ones.

**Configuration**

- **AC-22** Given the running API, when the two configuration endpoints are read, then they return the effective values of the Options classes, and the serialized responses contain no connection string, key, password or credentialed URL (asserted by scanning for the configured secrets of the test factory).

**UI**

- **AC-23** Given a maintainer on `/admin`, then the Overview tab shows the health counts and the summary with a 24 h / 7 d / 30 d selector (default 7 d), a manual refresh, and an empty state when there is nothing.
- **AC-24** Given the Requests tab, when a row is activated, then the side panel opens with the inspector, the URL gains `?request={id}`, Escape closes it and focus returns to the row; opening the deep link directly opens the panel.
- **AC-25** Given the Failures tab, then a retry button appears only for rows with `canRetry`, asks no confirmation beyond the existing behaviour, and the list refreshes after the call.
- **AC-26** Given a tab whose endpoint fails, then that tab shows an `ErrorState` with retry and the other tabs keep working.

(Component tests in `RagAssistant.Web.Tests` cover AC-2 and AC-23 to AC-26 with the stub API; a smoke test covers the side panel's focus and Escape behaviour and the deep link, because bUnit cannot show focus.)

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Admin audit trail | None; the retry is logged by request logging only | A second admin write action, or a real deployment |
| Filtering requests by owner, evidence outcome or date range | Not offered | Maintainers ask for it, or volume makes the list hard to use |
| Evaluation results tab and "create evaluation case" | Not present | Phase 5 (steps 5.1 to 5.3) |
| Showing answer text or passage text to maintainers | Never shown | A diagnosed case that cannot be explained from the trace alone |
| Trace retention and pruning | Indefinite (ADR 0004 D4) | Any real deployment |
| Live updates in admin | Manual refresh | Maintainers keep refreshing during incidents |
| Index for `feedback=negative` and `status` filters | None | A slow query plan at real volume |
| Summary beyond 30 days or custom ranges | Not offered | Phase 5 comparisons |

## Open Questions

None.
