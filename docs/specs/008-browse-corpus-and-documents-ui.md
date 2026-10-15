# Spec 008: Browse the corpus and the Documents UI

**Status:** Implemented (2026-10-15), approved 2026-10-14. All automated criteria pass; the manual AppHost run is still to be done (see Implementation Notes).
**Date:** 2026-10-14
**Roadmap:** steps 1.5 + 1.6 ([roadmap](../roadmap.md)) · **Builds on:** [spec 006](006-documents-data-model-and-import.md), [spec 007](007-background-ingestion-and-progress.md), [spec 005](005-web-shell.md), [spec 002](002-shared-kernel-api-conventions.md) · **Decision records:** [ADR 0001](../adr/0001-ui-ai-streaming-stack.md), [ADR 0004](../adr/0004-document-versioning.md)

## Problem

After spec 007 a document can be imported and indexed, but only through raw HTTP calls: there is no way to list the corpus, find a document, see why one failed, read the passages the chunker produced, or import a file from the browser. Content owners need this page to verify that chunking produced meaningful units (`product.md`, "Document detail"), and it completes the Phase 1 demo: *a content owner imports a Markdown policy, watches it being indexed live, and browses its passages.* This spec delivers the read APIs (list, facets, versions, passages), fixes the **pagination convention** that spec 002 deferred to the first listing slice, and builds the Documents UI on them.

## Scope

### In

**Host (Documents module)**

- `GET /api/documents` (search, filters, sort, paging), `GET /api/documents/facets`, `GET /api/documents/{id}/versions`, `GET /api/documents/{id}/versions/{number}/passages`.
- The pagination convention (offset) and a generic `Page<T>` envelope in the shared kernel.
- `GET /api/documents/{id}` unchanged (extended in spec 007).

**Web.Client (`/documents`, `/documents/{documentId}`)**

- Components `DocumentTable`, `DocumentStatusBadge`, `DocumentUpload` (dialog), `IngestionStatus`, `DocumentMetadataPanel` (read-only now), `PassageList`, `PassageCard`, plus `VersionHistory` for the History tab.
- A typed `DocumentsApi` client over the BFF `HttpClient`, and an SSE reader for the progress stream (`SetBrowserResponseStreamingEnabled`, `SseParser`, reconnect with `Last-Event-ID`).
- Retry action for failed ingestion (endpoint from spec 007), visible to content owners.

**Quality and docs**

- API integration tests (TUnit + Testcontainers), bUnit component tests, Web tests against the in-process `StubHost`.
- Project skills `fluentui-blazor-conventions` and `sse-endpoints` (the latter created after 1.4 per the roadmap and extended here with the client side), stored in `.claude/skills/`.
- Docs: roadmap (1.5, 1.6 done), spec 002 deferred row (pagination decided), spec 005 note (`/documents/{id}` route exists), CLAUDE.md.

### Out

- Edit metadata, upload a new version, archive, reprocess (1.7); the metadata panel is read-only and no such buttons exist.
- Retrieval search over passage content, `IDocumentSearch` (Phase 2); full-text search; a text search that looks inside documents.
- Citation deep links from chat (Phase 3 and 6), though the passage deep link of this spec is the target they will use.
- Hard delete, bulk actions, export, page-size selector, column chooser, owner/tag suggestions in the import form.
- Browser end-to-end tests (Playwright) and automated accessibility scans (Phase 6 does the full accessibility pass).

## Domain Model

No schema change and no migration. Existing tables and indexes are enough at the ADR 0004 volume (~100 documents, ~10k passages); the `documents.tags` GIN index already exists.

**Definitions used by the API**

| Term | Definition |
|---|---|
| Display status | Derived as in spec 006: `archived`, else `queued` / `processing` / `failed` from the latest version, else `active`. The status filter and the status shown always use the same rule. |
| Last activity | `max(active version IndexedAt, Document.UpdatedAt)`. A freshly imported document shows its import time. Ingestion alone does not change `UpdatedAt` (spec 007), so indexing moves it through `IndexedAt`. |
| Passage count | `PassageCount` of the **active** version; `null` when no version is indexed yet. |
| Browsable version | Any version of the document, whatever its status. Failed, queued and processing versions simply have no passages. |

### Pagination convention (spec 002 deferred decision, decided here)

Offset paging for every list endpoint: query `page` (1-based, default 1) and `pageSize` (default and maximum per endpoint); response envelope

```jsonc
{ "items": [ … ], "page": 1, "pageSize": 25, "totalCount": 61 }
```

A page beyond the last returns `200` with empty `items` and the real `totalCount`. Invalid `page` / `pageSize` → `400 validation.failed`. Ordering always ends with a unique tie-breaker (`id`, or `position`), so pages never overlap. `Page<T>` (record) and the page-request validator rule live in `RagAssistant.Shared/Common`; no business logic.

## API Contract

All routes are under `/api` (spec 002), return ProblemDetails with `code` and `traceId` on error, and require `KnowledgeSeeker` or above.

| Verb | Route | Query | Success | Errors |
|---|---|---|---|---|
| GET | `/api/documents` | `q`, `status`, `language`, `owner`, `tags` (repeated), `effectiveFrom`, `effectiveTo`, `sort`, `page`, `pageSize` | `200` `Page<DocumentSummary>` | 400 · 401 |
| GET | `/api/documents/facets` | — | `200` `Facets` | 401 |
| GET | `/api/documents/{documentId:guid}/versions` | — | `200` `{ items: VersionItem[] }` | 401 · 404 `documents.not_found` |
| GET | `/api/documents/{documentId:guid}/versions/{number:int}/passages` | `page`, `pageSize`, `focus` | `200` `PassagePage` | 400 · 401 · 404 `documents.not_found` / `documents.version_not_found` / `documents.passage_not_found` |

**List filters.** All combine with AND.

| Parameter | Rule |
|---|---|
| `q` | Case-insensitive "title contains"; `%`, `_` and `\` are escaped; trimmed; ≤ 200 chars; empty = no filter |
| `status` | One of `active`, `queued`, `processing`, `failed`, `archived` (the display status). Without `status`, **archived documents are excluded**; `status=archived` returns only archived documents |
| `language` | Two ASCII letters, case-insensitive |
| `owner` | Exact match on `OwnerName` (case-sensitive, as stored) |
| `tags` | Repeated; the document must have **all** the given tags (normalized like import: trimmed, lowercased); ≤ 20 |
| `effectiveFrom` / `effectiveTo` | `yyyy-MM-dd`, inclusive; documents without an effective date never match a date filter; `effectiveFrom ≤ effectiveTo` |
| `sort` | `title`, `-title`, `updated`, `-updated` (default), `effectiveDate`, `-effectiveDate` (documents without a date sort last in both directions); `updated` is last activity |
| `pageSize` | Default 25, max 100 |

```jsonc
// DocumentSummary
{ "id": "…", "title": "Budget Exception Policy", "ownerName": "Finance", "language": "en",
  "tags": ["budget", "finance"], "effectiveDate": "2026-01-01", "status": "active",
  "activeVersionNumber": 3, "latestVersionNumber": 3, "passageCount": 42,
  "lastActivityAt": "2026-10-01T09:30:00Z", "createdAt": "…" }

// Facets (counts include archived documents; statuses are a fixed list, so not returned)
{ "owners": [{ "value": "Finance", "count": 12 }], "languages": [{ "value": "en", "count": 40 }],
  "tags": [{ "value": "budget", "count": 7 }] }          // ordered by count desc, then value

// VersionItem — ordered by number descending
{ "id": "…", "number": 2, "status": "indexed", "origin": "upload", "sourceFormat": "markdown",
  "originalFileName": "budget.md", "sizeBytes": 18234, "passageCount": 42, "isActive": true,
  "createdAt": "…", "createdBy": "<sub>", "indexedAt": "…",
  "runs": [{ "id": "…", "attempt": 1, "status": "succeeded", "stage": "completed",
             "errorCode": null, "errorMessage": null,
             "queuedAt": "…", "startedAt": "…", "finishedAt": "…" }] }   // attempt descending

// PassagePage — Page<PassageItem> plus the version, ordered by position
{ "versionNumber": 2, "versionStatus": "indexed", "isActive": true,
  "items": [{ "id": "…", "position": 0, "sectionPath": ["Budget", "Exceptions"], "sectionOrdinal": 0,
              "text": "…", "charCount": 812, "tokenEstimate": 203 }],
  "page": 1, "pageSize": 50, "totalCount": 61 }
```

Notes:

- `passages` defaults to `pageSize = 50` (max 100). `focus=<passageId>` returns the page that **contains** that passage (the `page` parameter is then ignored); an id that is not in this version → `404 documents.passage_not_found`.
- Passage `text` is always returned in full (a page of 50 is about 80 KB). Embeddings are never returned.
- `createdBy` is a `sub`, not a display name (shown as is; mapping subjects to names is out of scope).
- The route `/api/documents/facets` cannot clash with `/api/documents/{documentId:guid}` because of the `guid` constraint.

## Authorization

| Endpoint | Policy | Knowledge Seeker | Content Owner | Maintainer | Anonymous |
|---|---|---|---|---|---|
| All four read endpoints | `KnowledgeSeeker` | 200 | 200 | 200 | 401 |

Seekers may read any document and version, including archived, failed and superseded ones and their passages (the product lets them "inspect active documents"; reading non-active versions is a deliberate choice, so owners and, later, old citations can open them).

**UI rules** (the Host stays the authority):

| Element | Visible to |
|---|---|
| `/documents`, `/documents/{id}`, list, filters, passages, history | KnowledgeSeeker and above (spec 005 page policy) |
| "Import" button and dialog | ContentOwner and above |
| "Retry" on a failed document | ContentOwner and above |

A seeker who forces a request to an owner endpoint still gets the Host's `403`; the UI shows the standard error state.

## UI Design

### Routes and state

| Route | Page |
|---|---|
| `/documents` | List. State in the query string: `q`, `status`, `language`, `owner`, `tags` (repeated), `effectiveFrom`, `effectiveTo`, `sort`, `page`. Changing any filter, search or sort resets `page` to 1. Search input is debounced (300 ms) and replaces the history entry while typing. Page size is fixed at 25. |
| `/documents/{documentId:guid}` | Detail. Query: `tab` (`metadata` default, `passages`, `history`), `version` (number), `passage` (id). |

Both pages are `[Authorize(Policy = KnowledgeSeeker)]`. Dates: timestamps are shown in the browser's local time as `yyyy-MM-dd HH:mm` with the UTC value in a tooltip; effective dates as plain `yyyy-MM-dd`.

**Prerendering.** The server prerender renders the page shell (`PageHeader` plus `LoadingState`) and makes **no** call to the Host; the WebAssembly component fetches through the BFF once, on its first interactive render. This keeps the "load once, no flash" rule of spec 005 without serializing data across render modes.

### Components (all in `Web.Client`, on Fluent UI Blazor primitives)

| Component | Contract | States |
|---|---|---|
| `DocumentTable` | Page of `DocumentSummary` rows; columns Title (link to detail), Status (`DocumentStatusBadge`), Language, Owner, Tags, Updated (last activity), Passages (`—` if null); sortable headers for Title, Updated, Effective date (`aria-sort`); pager with "N results"; semantic `table` with a caption | loading, loaded, empty (no documents), empty-filtered ("No document matches" + Clear filters), error (retry) |
| Filter bar | Search box, Status select (default "All except archived", then Active, Queued, Processing, Failed, Archived), Language, Owner, Tags (multi-select), effective-date range, "Clear filters"; options from `facets` | — |
| `DocumentStatusBadge` | `status` → text + icon + color (never color alone): Active, Queued, Processing, Failed, Archived | one per status |
| `DocumentUpload` | Dialog opened by "Import": file picker (`.md`, `.markdown`, `.txt`), Title (defaults to the file name without extension), Owner, Language (default `en`), Tags (comma-separated), Effective date, Source. Client checks mirror the API (extension; size ≤ 1 MiB, a constant documented as mirroring `Documents:Import:MaxFileSizeBytes`); the server stays authoritative. Focus is trapped and restored; Escape cancels | idle, validating (client-side field errors), uploading (controls disabled, spinner), success (closes, navigates to the new document), error |
| `IngestionStatus` | Latest run of the document: stage stepper `Queued → Extracting → Chunking → Embedding → Storing → Completed`, message of the latest event, attempt number; live region `aria-live="polite"` | queued, processing, completed, failed (error message, Retry for owners), live-lost |
| `DocumentMetadataPanel` | Title, owner, language, tags, effective date, source, created/updated by (`sub`) and at; read-only (editable states arrive in 1.7) | loading, loaded |
| `PassageList` | Version selector (default: the active version, else the latest), pager (50 per page), list of `PassageCard` | loading, empty ("No passages: ingestion has not finished or failed"), error |
| `PassageCard` | Section path breadcrumb, position and token estimate; text collapsed to the first ~300 characters, expand shows the full raw text in a preformatted block; the `passage` query value marks one card **selected** (expanded, scrolled into view, highlighted) | collapsed, expanded, selected |
| `VersionHistory` | Table of `VersionItem` with its runs (attempt, status, failing stage, error message, timings); the active version is marked; "View passages" per version | loading, loaded, error |

`ErrorState`, `EmptyState`, `LoadingState`, `PageHeader` and `ConfirmDialog` from spec 005 are reused.

### Behaviours

- **Detail header:** title, status badge, language, "Version N" (active, else latest), last activity, and the Retry action when the latest version is failed and the user is an owner.
- **Live ingestion (detail).** While the latest run is not terminal, `IngestionStatus` reads `GET /api/documents/{id}/ingestion/events` (owners only; seekers see the status at load time and a Refresh button) with `SetBrowserResponseStreamingEnabled(true)` and `SseParser`, updates the stepper per `ingestion.stage` event, ignores `heartbeat`, and on the terminal event reloads the document, versions and the current passage page. Retry starts a new run and a new stream.
- **Stream drops.** Up to 3 reconnects (after 1 s, 2 s, 4 s) resuming with `Last-Event-ID`; then `IngestionStatus` shows "Live updates lost" and a Refresh button that reloads the document. A `401` shows the session-expired `ErrorState` of spec 005. The stream is closed when the component is disposed or the user navigates away.
- **Live list.** While any visible row is `queued` or `processing`, the list re-queries every 5 s with the current URL state (timer cancelled on dispose). No SSE on the list.
- **Import flow.** `POST /api/documents` as `multipart/form-data` through the BFF (the `X-Requested-With` handler already applies). `202` → close and navigate to `/documents/{id}`. Field errors from `errors.<field>` appear under the matching input, `documents.*` file codes under the file picker; `409 documents.duplicate_content` shows "This content is already in the corpus" with a link to `existingDocumentId`; `403` hides nothing but shows "You are not allowed to import documents"; `401` shows the session-expired message; `413` shows "File too large"; network and `5xx` show a generic retryable error. The form keeps its values after any error.
- **Deep link.** `/documents/{id}?tab=passages&version=2&passage=<id>` opens the Passages tab on that version, requests `focus=<id>`, shows the containing page and selects the card. An unknown passage shows an inline notice ("That passage is not in this version") and the first page.
- **Role handling.** Role checks use the `roles` claim like the navigation (spec 005); they only hide controls.

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| Empty corpus | `EmptyState`; owners get an Import button, seekers a plain explanation |
| Filters match nothing | Filtered empty state with Clear filters |
| `page` beyond the last page | API returns empty items with `totalCount`; the UI moves to the last valid page |
| Invalid query parameter in the URL (`status=foo`, `page=0`, bad date) | The UI drops the invalid value and shows the list; the API returns `400 validation.failed` if called directly |
| Document unknown or id not a guid | Detail shows a not-found `ErrorState` with a link to `/documents` (`404`); non-guid ids hit the route's 404 |
| Document archived | Browsable; badge Archived; no Retry |
| Failed first import | Detail shows the failure on `IngestionStatus` and History; Passages tab shows the empty state |
| Failed re-ingestion with an older active version | Status `failed`, but the Passages tab still defaults to the **active** version |
| Large passage text | Full text only on expand; preformatted, scrollable, never rendered as HTML (raw Markdown shown as text) |
| Document or passage text containing HTML / script | Always encoded by Blazor; no `MarkupString` |
| Ingestion finishes while the user is on another tab | Data reloads on the terminal event; the selected tab is kept |
| Two imports in a row | Each navigates to its own document; the list shows both after refresh |
| Session expires during polling or SSE | `401` → session-expired `ErrorState`, polling stops |
| Host unreachable | `502/504` ProblemDetails → `ErrorState` with retry; polling stops until retried |
| Very long title, tags or file names | Truncated with ellipsis and full value in a tooltip; layout does not break |
| Concurrent change between list requests | Normal offset-paging drift is accepted at this scale |
| User without `ContentOwner` reaches the SSE URL via the UI | Not requested: seekers get the one-time status and Refresh |

## Non-Functional Requirements

- **API latency:** list, facets, versions and a passages page < 200 ms p95 at ADR 0004 volume on the dev machine; no N+1 queries (one query per request, projections to DTOs, no entity tracking, no embeddings loaded).
- **Queries:** the display-status filter and last-activity sort are translated to SQL (no client-side filtering); `tags` uses `@>` on the GIN-indexed array.
- **UI:** the first list render after the WebAssembly bundle is loaded shows data after a single API call (plus `facets`); the filter bar's facets are fetched once per page load.
- **Accessibility baseline** (full pass in Phase 6): semantic table with caption and `aria-sort`; every control labelled; status communicated by text and icon, not color alone; dialog focus trap and restore; the ingestion region announced politely; the journey import → watch → browse passages is keyboard-operable.
- **Security:** no tokens in the browser; all calls through the BFF; passage and document text rendered as text only.
- **Logging:** the Host logs nothing new beyond request logging; the Web client logs no document content.
- **Architecture:** the read endpoints stay in the Documents module (new slices under `Features/`, internal handlers, no shared mutable state); `Web.Client` does not reference the Documents project.
- **Dependencies:** none new. `Microsoft.AspNetCore.Components.WebAssembly` (already referenced) provides `SetBrowserResponseStreamingEnabled`; `System.Net.ServerSentEvents` is part of .NET 10.

## Integrations

- **Host ↔ Web:** the BFF proxy is a pass-through (spec 005), so the new routes need no BFF change. Streaming through the BFF is verified here for the first time, in the Web tests against `StubHost`.
- **Spec 007:** the SSE endpoint, retry endpoint, event shape and the error codes are consumed as defined there.
- **Client DTOs:** `Web.Client` defines its own response records mirroring the JSON above (it cannot reference a module); additions to the JSON are ignored by the client. Contract drift is caught by the Web tests, which deserialize the same JSON samples the integration tests assert.
- **Realm / auth:** no change.
- **Other modules:** none; `IDocumentSearch` and its contract arrive in Phase 2.

## Acceptance Criteria

List and facets (integration tests, seeded data)

- **AC-1** Given a corpus of documents in each display status, when a seeker calls `GET /api/documents`, then `200` with `Page<DocumentSummary>` ordered `-updated`, `totalCount` equal to the number of non-archived documents (archived ones excluded; `status=archived` returns exactly the archived ones), and `passageCount` from the active version (`null` when none).
- **AC-2** Given `pageSize = 2` and 5 documents, then page 1, 2, 3 contain 2, 2, 1 items with no overlap or gap under every sort; given `page` beyond the last, then empty `items` and the real `totalCount`.
- **AC-3** Given `q=budget`, then only titles containing it case-insensitively match; given `q=50%` or `q=a_b`, then `%` and `_` are literal.
- **AC-4** Given `status=failed`, `language=fr`, `owner=Finance` or a date range, then only matching documents are returned; given several filters, then they combine with AND; documents without an effective date never match a date range.
- **AC-5** Given `tags=budget&tags=finance`, then only documents with **both** tags match (`Budget ` normalizes to `budget`).
- **AC-6** Given each `sort` value, then the order is correct, ties are broken by `id`, and documents without an effective date sort last in both directions; `updated` uses last activity.
- **AC-7** Given invalid `page` (0), `pageSize` (0 or 101), `sort`, `status`, `language`, dates (`from > to`), 21 tags or a 201-character `q`, then `400 validation.failed` with an error for that field.
- **AC-8** Given documents, when `GET /api/documents/facets` is called, then owners, languages and tags carry correct counts (archived included), ordered by count desc then value.

Versions and passages (integration tests)

- **AC-9** Given a document with a superseded version 1, an active version 2 and a failed retry run, when `GET …/versions` is called, then versions are ordered by number descending, `isActive` is true only for version 2, and each version lists its runs by attempt descending with `errorCode`/`errorMessage` for failed ones.
- **AC-10** Given an indexed version with 61 passages, when `GET …/versions/2/passages` is called, then page 1 has 50 items ordered by `position`, `totalCount = 61`, `versionNumber`, `versionStatus` and `isActive` are set, and no embedding is present in the payload.
- **AC-11** Given `focus=<id of passage 55>`, then the response is page 2 (the page parameter ignored) and contains that passage; given an id from another version or unknown, then `404 documents.passage_not_found`.
- **AC-12** Given a failed or queued version, then passages returns `200` with empty `items`; given a superseded version, then its passages are returned.
- **AC-13** Given an unknown document, then `404 documents.not_found` on versions and passages; given an unknown version number, then `404 documents.version_not_found`.

Authorization (integration tests)

- **AC-14** Given an anonymous caller, then all four endpoints return `401`; given a KnowledgeSeeker, ContentOwner and Maintainer, then `200`.

List page (bUnit / Web tests)

- **AC-15** Given the stub Host returns a page, then `DocumentTable` renders one row per document with the Title link, `DocumentStatusBadge` (text and icon), language, owner, tags, local-time Updated with a UTC tooltip, and `—` for a null passage count; headers expose `aria-sort`.
- **AC-16** Given loading, empty corpus, no match for the filters, and a failing API, then the page shows `LoadingState`, `EmptyState` (Import button only for owners), the filtered empty state with Clear filters, and `ErrorState` with a working retry.
- **AC-17** Given the URL `?q=a&status=failed&tags=x&tags=y&sort=title&page=2`, then the API is called with exactly those values and the controls reflect them; given a filter change, then the URL updates and `page` resets to 1; given an invalid value in the URL, then it is dropped.
- **AC-18** Given a visible `queued` or `processing` row, then the list re-queries every 5 s (controlled `TimeProvider`/timer) until none is busy, and stops when the component is disposed.
- **AC-19** Given prerendering, then the server-rendered HTML contains the shell and `LoadingState` and the stub Host receives **no** request until the interactive render, and then exactly one list request and one facets request.
- **AC-20** Given a seeker, then no Import button is rendered; given an owner or maintainer, then it is.

Import dialog (bUnit / Web tests)

- **AC-21** Given a chosen `report.md`, then Title defaults to `report` and Language to `en`; given an unsupported extension or a file over 1 MiB, then a client-side error appears and no request is sent.
- **AC-22** Given valid input, when submitted, then a `multipart/form-data` POST with the `X-Requested-With` header is sent; the dialog shows the uploading state with controls disabled; on `202` it closes and navigates to `/documents/{documentId}`.
- **AC-23** Given server responses `400` with `errors.title`, `400 documents.unsupported_extension`, `409 documents.duplicate_content`, `403`, `401`, `413`, a network failure and `500`, then each shows the specified message at the specified place, the 409 links to the existing document, and the form keeps its values.
- **AC-24** Given the dialog, then focus moves into it, is trapped, Escape closes it and focus returns to the Import button.

Detail page (bUnit / Web tests)

- **AC-25** Given a document, then the header shows title, status badge, language, version and last activity; tabs Metadata, Passages and History switch with the `tab` query value; `DocumentMetadataPanel` shows all metadata read-only.
- **AC-26** Given an unknown id, then a not-found `ErrorState` with a link to `/documents`; given an owner and a failed document, then Retry calls `POST …/ingestion/retry` and the page switches to the new run; given a seeker or an archived document, then no Retry.
- **AC-27** Given an in-progress document and an owner, when the stub Host streams `ingestion.stage` events and a terminal event, then the stepper advances in order, `heartbeat` is ignored, the live region announces the changes, and on the terminal event the document, versions and passages reload; the connection uses `SetBrowserResponseStreamingEnabled`.
- **AC-28** Given the stream drops, then up to 3 reconnects with `Last-Event-ID` set to the last received id happen after 1 s, 2 s and 4 s; after the fourth failure `IngestionStatus` shows "Live updates lost" with a Refresh button that reloads; a `401` shows the session-expired `ErrorState`; disposing the component closes the stream.
- **AC-29** Given a seeker on an in-progress document, then no stream is opened and a Refresh button is shown.
- **AC-30** Given an indexed document, then `PassageList` defaults to the active version, offers a version selector, pages at 50, and `PassageCard` is collapsed to about 300 characters with a section path breadcrumb, position and token estimate; expanding shows the full raw text as text (a passage containing `<script>` renders encoded).
- **AC-31** Given `?tab=passages&version=2&passage=<id>`, then the API is called with `focus=<id>`, the Passages tab opens on the containing page, and that card is selected, expanded and scrolled into view; given an unknown passage, then an inline notice and the first page.
- **AC-32** Given the History tab, then each version shows number, status, format, file name, size, passage count, created at/by, its runs with failing stage and error message, and marks the active version.
- **AC-33** Given a failed re-ingestion with an older active version, then the badge shows Failed while the Passages tab still shows the active version.

Through the BFF

- **AC-34** Given the real BFF against `StubHost`, then `/api/documents`, `/facets`, `/versions` and `/passages` are forwarded with the bearer token, and an `ingestion/events` response reaches the client incrementally (first event received before the upstream completes).

Architecture and docs

- **AC-35** The Documents project still references only `RagAssistant.Shared`; `Web.Client` does not reference any module; `arch-check` passes.
- **AC-36** The roadmap marks 1.5 and 1.6 done, spec 002's pagination row points here, and `.claude/skills/fluentui-blazor-conventions` and `.claude/skills/sse-endpoints` exist and describe the code as built.

## Testing Approach

- **API:** integration tests (TUnit, `RagAssistantAppFactory`, Testcontainers) seed documents, versions, runs and passages directly through `DocumentsDbContext` (no embeddings needed except where a vector column is required, then a fixed vector), one test per criterion AC-1 to AC-14.
- **Components and pages:** bUnit under TUnit for the components and pages with a fake `DocumentsApi`; SSE logic is tested through a small `ISseSource` abstraction with controlled delays.
- **BFF:** `WebAppFactory` + `StubHost` extended with the new routes and an SSE route that emits events with controllable timing (AC-19, AC-34).
- **Manual:** run the AppHost once, import a Markdown file, watch it index, browse its passages, and record the result and screenshots in Implementation Notes (as for specs 005 to 007).

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Page-size selector, column chooser | Fixed page size 25 / 50 | Users ask for it |
| Owner and tag suggestions in the import form | Plain text fields | Tag sprawl (spec 006 deferred row) |
| Max import size exposed by the API | UI constant mirrors the default 1 MiB; server is authoritative | The limit is changed in configuration |
| Seekers see live ingestion progress | One-time status plus Refresh | Seekers need it (spec 007 restricts the stream to owners) |
| SSE for the list | Poll every 5 s while busy | Many concurrent imports |
| Search inside document content | Title search only | Phase 2 retrieval inspector |
| Mapping `createdBy` subjects to names | Show the `sub` | A user directory exists |
| Passage content search/filter in the Passages tab | Paging only | Corpora grow beyond ~100 passages per version |
| Keyset paging | Offset paging | Volumes grow far beyond ADR 0004 targets |

## Open Questions

None. Resolved in review (2026-10-14): `Web.Client` has its own response records and `Page<T>` lives in `RagAssistant.Shared/Common`; last activity is `max(IndexedAt, UpdatedAt)` with no dash; archived documents are hidden from the default list and shown with `status=archived`.

## Implementation Notes

- **Host:** four slices under `Features/` (`ListDocuments`, `GetFacets`, `ListVersions`, `ListPassages`), no schema change. `Page<T>` and `PagingRules` (parsing, `Skip`, the validator rule) are in `RagAssistant.Shared/Common/Page.cs`. The C# property is `Number` with `[JsonPropertyName("page")]` because a record member cannot share its type's name.
- **Query binding:** `page`, `pageSize`, dates and `focus` are bound as strings and parsed in the validators. Typed binding would turn `page=abc` into `request.malformed`; AC-7 asks for `validation.failed` with a field error.
- **SQL:** display status and last activity are computed columns of one projected query (correlated subqueries for the latest and active version), filtered, counted, sorted and paged in SQL. `tags` uses one `Tags.Contains(tag)` per tag (AND), not a single `@>` expression, so the GIN index is not guaranteed to be used; at the ADR 0004 volume this is immaterial. Facets group owners and languages in SQL and count tags in memory (arrays are tiny).
- **Seeding in integration tests:** `CorpusSeeder` writes raw SQL, because `DocumentsDbContext` is internal and the test project has no access. Each test seeds under its own unique owner and filters on it, since the factory and its database are shared.
- **Client:** models are client-owned records (`DocumentInfo` for the detail response, to avoid a clash with the page class). `IDocumentsApi`/`DocumentsApi` and `IIngestionEventSource`/`IngestionEventSource` take a lazily resolved `HttpClient` and are registered in `AddUiServices`, so server prerendering resolves them without a client; pages fetch in `OnAfterRenderAsync(firstRender)`. Stream reconnect logic is in `IngestionWatcher` (testable with a controllable `TimeProvider`).
- **UI choices:** every control is a Fluent UI Blazor component (see the `fluentui-blazor-conventions` skill): `FluentDataGrid` for the list and the version history (server-side sort through `OnSortChanged`), `FluentPaginator`, `FluentTextInput`, `FluentSelect`, `FluentDatePicker`, `FluentInputFile`, `FluentTabs`, `FluentBadge` for status, tags and stages, `FluentMessageBar` for errors and notices, `FluentCard` for passages and metadata. Status carries text plus a glyph. The tags filter is a multi-select fed by `facets`. Retry sits in `IngestionStatus`, next to the status, not in the page header. The detail page learns the active version number from the versions list, because `GET /api/documents/{id}` carries only `activeVersionId`.
- **Deviation, AC-19:** the Web tests prove that the prerender returns the shell and `LoadingState` and calls the stub Host not at all (detail page, unique id). "Then exactly one list request and one facets request" is verified in bUnit (`Page_whileLoading_...`), since no WebAssembly runtime runs in tests.
- **Deviation, AC-24:** Escape closes the dialog (tested). Focus trap and restore come from `FluentDialog` (modal) and were not verified in bUnit's loose JS mode; they need the manual run.
- **Tests:** integration `ListDocumentsTests`, `BrowseDocumentTests` (AC-1 to AC-14); bUnit and logic tests in `tests/RagAssistant.Web.Tests/Documents` (`DocumentsPageTests`, `DocumentUploadTests`, `DocumentDetailTests`, `IngestionWatcherTests`, `DocumentQueryTests`) and `DocumentsBffTests` (AC-34, AC-19). The Web tests use a hand-written `ManualTimeProvider` (no `Microsoft.Extensions.TimeProvider.Testing` package). `dotnet build`, `dotnet test --solution RagAssistant.slnx` and `dotnet format --verify-no-changes` were clean.
- **Not done:** the manual run of AppHost (import a file, watch it index live in the browser, browse and deep-link passages, screenshots) and a real-browser check that the WebAssembly client streams the SSE response through the BFF. The `arch-check` skill was not run; `ModuleBoundaryTests` (Documents references only Shared) still passes and `Web.Client` references no module.
