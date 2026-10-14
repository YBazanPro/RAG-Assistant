# Spec 009: Document lifecycle (edit, new version, archive, restore, reprocess)

**Status:** Approved (2026-10-14)
**Date:** 2026-10-14
**Roadmap:** step 1.7 ([roadmap](../roadmap.md)) · **Builds on:** [spec 006](006-documents-data-model-and-import.md), [spec 007](007-background-ingestion-and-progress.md), [spec 008](008-browse-corpus-and-documents-ui.md) · **Decision records:** [ADR 0004](../adr/0004-document-versioning.md)

## Problem

Documents change and content owners cannot keep the corpus truthful: a typo in a title cannot be fixed, a revised policy cannot replace the old one, an obsolete document cannot be taken out of circulation, and a document cannot be re-indexed after the chunking or embedding configuration changes. Today the only way to change anything is to import a new document and leave the old one behind. This spec delivers the lifecycle ADR 0004 promised (versions with preserved history, archive, only-active-is-retrievable) and the UI to drive it, completing Phase 1.

## Scope

### In

**Host (Documents module)**

- `PUT /api/documents/{id}`: edit metadata, with optimistic concurrency (`If-Match` / `ETag`).
- `POST /api/documents/{id}/versions`: upload a new version (file only).
- `POST /api/documents/{id}/reprocess`: new version from the latest indexed content.
- `POST /api/documents/{id}/archive` and `POST /api/documents/{id}/restore`.
- Migration: `documents.revision` (int, default 1), the concurrency token for metadata edits.
- `GET /api/documents/{id}` returns an `ETag`; `GET /api/documents` and `GET …/{id}` are unchanged otherwise.
- Telemetry: counters for lifecycle actions; structured logs.

**Web.Client**

- `DocumentMetadataPanel` becomes editable (edit, save, cancel, saving, conflict).
- Detail-page actions for content owners: **Upload new version** (dialog), **Reprocess**, **Archive** / **Restore** (confirm dialogs), on the existing detail page only.
- `DocumentUpload` gains a "new version" mode (file only).

**Quality and docs**

- Integration tests (TUnit + Testcontainers), bUnit and Web tests.
- Docs: roadmap (1.7 done, Phase 1 complete), CLAUDE.md Documents line, specs 007 and 008 cross-notes.

### Out

- PDF/DOCX (1.8); hard delete; bulk actions; row actions in the list.
- Cancelling a running ingestion; draft/publish review step (spec 006 deferred).
- Per-version download of the original file; diff between versions.
- A separate lifecycle audit table (who changed what): `UpdatedAt` / `UpdatedBy` and the versions list are the audit trail for now.
- Pruning old versions' passages (kept; see Deferred Decisions).
- Citation snapshots and "this source has changed" UX (Phase 3 and 6).

## Domain Model

### Schema change

One migration (`/migrate`): `documents.revision integer not null default 1`, mapped as a concurrency token. It is incremented **only** by metadata edits (the worker's updates to `ActiveVersionId` do not touch it, so finishing an ingestion never invalidates an edit in progress). The ETag is the strong value `"<revision>"`.

### Operations and their effects

| Operation | Effect |
|---|---|
| **Edit metadata** | Replaces title, ownerName, language, tags, effectiveDate and source with the normalized values (same rules as import); `Revision++`, `UpdatedAt`, `UpdatedBy`. Nothing is re-embedded. Allowed on archived documents. |
| **New version** | New `DocumentVersion` (`Number = max + 1`, `Origin = Upload`, `Status = Queued`) with the normalized content of the file; `LatestContentHash` = its hash; new run (`Attempt = 1`, `Queued`) and event `Queued/Completed`; `UpdatedAt`, `UpdatedBy`. The current active version stays active and retrievable until the new one is indexed (spec 007 then supersedes it). |
| **Reprocess** | New version `Number = max + 1`, `Origin = Reprocess`, copying `Content`, `ContentHash`, `SizeBytes`, `SourceFormat`, `OriginalFileName` and `ContentType` from the latest version, which must be `Indexed`; `LatestContentHash` is unchanged; new run and event as above; `UpdatedAt`, `UpdatedBy`. |
| **Archive** | `Status = Archived`, `UpdatedAt`, `UpdatedBy`. Nothing is deleted. Retrieval excludes the document (spec 006 rule: `Status = Active`). Its content hash leaves the duplicate guard (partial unique index on active documents), so the same content can be imported again. |
| **Restore** | `Status = Active`, `UpdatedAt`, `UpdatedBy`. The active version becomes retrievable again, without re-indexing. If another **active** document now has the same latest content, the partial unique index rejects it: `409 documents.duplicate_content` with `existingDocumentId`. |

Rules shared by the version-creating operations (new version, reprocess):

- The document must be `Active` (archived → `409 documents.archived`; restore first).
- The latest version must not be `Queued` or `Processing` (`409 documents.ingestion_in_progress`). Two concurrent calls produce one `202` and one `409`: the unique index on (`DocumentId`, `Number`) catches the race and is mapped to that same `409`.
- A `Failed` latest version does not block: a new upload replaces it as the latest version, and the failed one stays in history. (Retry from spec 007 is the way to re-run the same content.)
- Everything happens in one transaction, followed by the channel write after commit (spec 007). Content rules match import (size, UTF-8, extensions, normalization, hash).

**In-flight ingestion and archive.** Archive is allowed while a run is queued or running. The run finishes and indexes normally (spec 007 behaviour, resolving its edge case "Document archived before its run starts"); the document stays `Archived`, so nothing is retrievable. A later restore finds the content already indexed. Archive and indexing write different columns (`Status` versus `ActiveVersionId`), so they cannot overwrite each other.

**Display status** (unchanged, spec 006): an archived document shows `archived` whatever its versions say.

### Duplicate rules for a new version

| Content equals | Result |
|---|---|
| The latest version of **this** document | `409 documents.duplicate_content`, `existingDocumentId` = this document |
| The latest content of **another active** document | `409 documents.duplicate_content`, `existingDocumentId` = that document (unique-index race mapped the same way) |
| An older, non-latest version of this document (a rollback) | Accepted: it becomes a new version |
| An archived document's content | Accepted |

## API Contract

All routes are under `/api` (spec 002) and return ProblemDetails with `code` and `traceId`.

| Verb | Route | Request | Success | Errors |
|---|---|---|---|---|
| PUT | `/api/documents/{documentId:guid}` | JSON body, header `If-Match: "<revision>"` | `200` `GetDocument.Response`, new `ETag` | 400 · 401 · 403 · 404 · 412 `documents.edit_conflict` · 428 `documents.precondition_required` |
| POST | `/api/documents/{documentId:guid}/versions` | `multipart/form-data`: `file` | `202` `NewVersion.Response`, `Location: /api/documents/{id}` | 400 file codes (as import) · 401 · 403 · 404 · 409 `documents.duplicate_content` / `documents.archived` / `documents.ingestion_in_progress` · 413 |
| POST | `/api/documents/{documentId:guid}/reprocess` | — | `202` `NewVersion.Response` | 401 · 403 · 404 · 409 `documents.archived` / `documents.ingestion_in_progress` / `documents.not_reprocessable` |
| POST | `/api/documents/{documentId:guid}/archive` | — | `200` `GetDocument.Response` | 401 · 403 · 404 · 409 `documents.already_archived` |
| POST | `/api/documents/{documentId:guid}/restore` | — | `200` `GetDocument.Response` | 401 · 403 · 404 · 409 `documents.not_archived` / `documents.duplicate_content` |
| GET | `/api/documents/{documentId:guid}` (spec 006/007) | — | adds `ETag: "<revision>"`, `revision` in the body | unchanged |

```jsonc
// PUT body — same field rules as import (title 1–200, ownerName 1–100, language ISO 639-1,
// tags ≤ 20 × ≤ 50, effectiveDate yyyy-MM-dd or null, source ≤ 500 or null)
{ "title": "Budget Exception Policy", "ownerName": "Finance", "language": "en",
  "tags": ["budget", "finance"], "effectiveDate": "2026-01-01", "source": null }

// 202 — NewVersion.Response
{ "documentId": "…", "versionId": "…", "versionNumber": 2, "ingestionRunId": "…", "status": "queued" }
```

Notes:

- `not_reprocessable` means the latest version is not `Indexed` (failed → use retry; none indexed yet → wait or upload again).
- A missing `If-Match` is `428`; a value that does not match the current revision is `412` and the body names the current revision so the client can reload. `If-Match: *` is not supported (treated as missing).
- The Host endpoints use `.DisableAntiforgery()` (multipart) and rely on the BFF CSRF header (spec 005), like import.
- The metadata PUT validates with FluentValidation exactly like import (`validation.failed`, errors under field names). The new-version endpoint reuses the import file rules and codes (`documents.file_required`, `documents.unsupported_extension`, `documents.file_too_large`, `documents.invalid_utf8`, `documents.empty_content`).

## Authorization

| Endpoint | Policy | Knowledge Seeker | Content Owner | Maintainer | Anonymous |
|---|---|---|---|---|---|
| `PUT`, `…/versions`, `…/reprocess`, `…/archive`, `…/restore` | `ContentOwner` | 403 | 200 / 202 | 200 / 202 | 401 |

Any content owner may act on any document (ADR 0002; no per-document ownership). `UpdatedBy` and `CreatedBy` on new versions hold the caller's `sub`. The UI shows the controls to owners and maintainers only; the Host remains the authority.

## UI Design

Extends the detail page of spec 008. Actions are on the **detail page only** (no row actions in the list).

| Element | Behaviour |
|---|---|
| Actions menu (header) | **Upload new version**, **Reprocess**, **Archive** (or **Restore** when archived); owners and maintainers only. Disabled with an explanatory tooltip when not allowed by the current state: no new version or reprocess while the latest version is queued/processing or the document is archived; reprocess also needs an indexed latest version. Retry (spec 008) stays next to the status. |
| `DocumentMetadataPanel` | Read-only by default; **Edit** switches to a form (same field rules and client checks as the import dialog, tags comma-separated), **Save** / **Cancel**. States: loading, loaded, editable, saving, conflict. Save sends `PUT` with `If-Match` taken from the last `ETag`; on `200` the panel shows the saved values and the page header refreshes. |
| Conflict (412) | A banner "This document was changed by someone else" with **Reload** (discards the edit and shows the current values) and **Keep editing** (the user's input stays, the ETag is refreshed on the next save attempt after Reload only). |
| Upload new version | `DocumentUpload` in "new version" mode: only the file picker (same extension and size checks), the document title is shown as context. On `202` the dialog closes, the page shows the new version in `IngestionStatus` and opens the progress stream (owners). Errors map as for import (409 duplicate links to `existingDocumentId`; 409 archived / in-progress show a message and refresh the document). |
| Reprocess | `ConfirmDialog`: "Reprocess builds a new version from the stored content. The current version stays available until the new one is indexed." On `202` the same flow as a new version. |
| Archive | `ConfirmDialog` (destructive style): "Archived documents are no longer used to answer questions. You can restore it later." On `200` the badge becomes Archived and the actions update. |
| Restore | Confirm dialog; `409 duplicate_content` shows "Another active document already has this content" with a link. |
| History tab | Shows the new versions (origin shown as Upload or Reprocess) with their runs, unchanged component from spec 008. |
| Lists and stale state | After any action the page reloads the document (ETag, status, versions) and the list is re-queried on navigation back. |

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| Edit with a stale `If-Match` | `412 documents.edit_conflict`; nothing changes |
| Two owners save the same revision at once | One `200`, the other `412` (concurrency token on `Revision`) |
| Edit without `If-Match` | `428 documents.precondition_required` |
| Edit changes only the case or whitespace of tags | Normalized; if the normalized set equals the stored set and nothing else changed, the call still succeeds (`Revision++`, no special no-op handling) |
| Edit an archived document | Allowed |
| New version/reprocess while queued or processing | `409 documents.ingestion_in_progress` |
| Two simultaneous uploads | One `202`, one `409 documents.ingestion_in_progress` |
| New version of an archived document | `409 documents.archived` |
| New version identical to the latest | `409 documents.duplicate_content` (own id) |
| New version fails ingestion | Previous active version stays active; the failed version is the latest; the document shows `failed`; the owner can retry (spec 007) or upload another version |
| Upload after a failed version | Allowed; the failed version stays in history |
| Reprocess when the latest version failed | `409 documents.not_reprocessable` (use retry) |
| Reprocess with the same configuration | Allowed; produces equivalent passages with new ids (ADR 0004: ids are never reused) |
| Archive while a run is active | Allowed; the run completes and indexes; the document stays archived |
| Archive an archived document / restore an active one | `409 documents.already_archived` / `documents.not_archived` |
| Restore when another active document has the same latest content | `409 documents.duplicate_content` + `existingDocumentId` |
| Restore a document whose latest version is failed | Allowed; its active version (if any) becomes retrievable again; status shows `failed` until retried |
| Restore a document that never had an indexed version | Allowed; it stays unretrievable until indexed |
| Client disconnect mid-upload | Request cancelled, nothing persisted (single transaction) |
| Unknown document id | `404 documents.not_found` |
| Large body | Same limits as import (`MaxFileSizeBytes` + 64 KB) → `413` |
| Migration on existing data | Existing rows get `revision = 1` |

## Non-Functional Requirements

- **Latency:** each lifecycle call < 500 ms p95 (new version of a 1 MB file like import; the others < 100 ms); no AI call in the request.
- **Concurrency:** the metadata concurrency token is `Revision` only; archive/restore and the worker write disjoint columns. All multi-row changes run in one transaction.
- **Telemetry:** counters on `Meter("RagAssistant.Documents")`: `documents.versions.created` (tag `origin` = `upload` / `reprocess`), `documents.metadata.updated`, `documents.archived`, `documents.restored`, and `documents.lifecycle.rejected` (tag `reason` = error code). `LoggerMessage` logs with document id, version id and action; never content.
- **Architecture:** all slices live in the Documents module (`Features/UpdateDocument`, `UploadVersion`, `ReprocessDocument`, `ArchiveDocument`, `RestoreDocument`); internal handlers; no new dependency; `arch-check` passes.
- **Dependencies:** none.
- **Accessibility baseline:** the actions menu, edit form and dialogs are keyboard-operable with labelled controls; conflict and saved messages are announced politely.

## Integrations

- **Spec 007 worker:** unchanged; it processes the new runs. Activation (supersede previous, set `ActiveVersionId`) is implemented in 007 and reused as is.
- **BFF:** no change (PUT, POST pass through; `If-Match` / `ETag` are forwarded as normal headers; the CSRF header applies to PUT/POST, already handled by the client handler).
- **Realm / auth:** none.
- **Other modules:** none; `IDocumentSearch` (Phase 2) relies on `Status = Active` and `ActiveVersionId`, which this spec maintains.

## Acceptance Criteria

Edit metadata

- **AC-1** Given an owner, a document at revision 1 and a valid body with `If-Match: "1"`, when `PUT`, then `200` with the new values (tags and language normalized), `revision = 2`, `ETag: "2"`, `UpdatedAt`/`UpdatedBy` changed, `CreatedAt`/`CreatedBy` unchanged, and no passage or version changes.
- **AC-2** Given invalid fields (empty title, 201-char title, invalid language, 21 tags, 51-char tag, bad date, 501-char source), then `400 validation.failed` with an error per field and nothing changes.
- **AC-3** Given `If-Match: "1"` after a successful edit (stale), then `412 documents.edit_conflict` and nothing changes; given no `If-Match`, then `428`; given two concurrent edits at revision 1, then exactly one `200` and one `412`.
- **AC-4** Given an archived document, then edit succeeds; given an unknown id, then `404`.
- **AC-5** Given ingestion finishing between a `GET` and a `PUT` (the worker updates `ActiveVersionId`), then the `PUT` still succeeds with the earlier ETag.

New version

- **AC-6** Given an active, indexed document and a different file, when `POST …/versions`, then `202` with `versionNumber = 2`, a queued version with `Origin = Upload`, a run with `Attempt = 1`, an `Queued/Completed` event, `LatestContentHash` updated, `UpdatedAt`/`UpdatedBy` changed, and the version 1 still `ActiveVersionId` until indexed.
- **AC-7** Given AC-6 and the worker running, then version 2 becomes `Indexed`, `ActiveVersionId` = version 2 and version 1 is `Superseded` with its passages kept.
- **AC-8** Given a `.txt` file for a Markdown document, then the new version has `SourceFormat = PlainText`.
- **AC-9** Given the same normalized content as the latest version (different line endings or BOM), then `409 documents.duplicate_content` with its own id; given content equal to another active document's latest content, then `409` with that id; given content equal to an older version of this document, then `202`; given content of an archived document, then `202`.
- **AC-10** Given file-rule violations (no file, `.pdf`, empty, invalid UTF-8, oversize), then `400` with the import codes and nothing persisted.
- **AC-11** Given a queued or processing latest version, then `409 documents.ingestion_in_progress`; given two simultaneous uploads, then exactly one `202` and one `409`; given an archived document, then `409 documents.archived`.
- **AC-12** Given a failed latest version, then a new upload is accepted as the next version and the failed one stays in history; given the new version fails, then the previous active version stays active and retrievable.

Reprocess

- **AC-13** Given an indexed latest version, when `POST …/reprocess`, then `202`, a new version `Origin = Reprocess` with the same `Content`, `ContentHash`, `SourceFormat`, `OriginalFileName` as the source, `LatestContentHash` unchanged, a new queued run; the old version stays active until the new one is indexed, then is `Superseded`, and the new passages have new ids.
- **AC-14** Given the latest version `Failed`, `Queued` or `Processing`, then `409 documents.not_reprocessable` (failed) or `documents.ingestion_in_progress` (queued/processing); given an archived document, then `409 documents.archived`.

Archive and restore

- **AC-15** Given an active document, when `POST …/archive`, then `200`, `status = "archived"`, `UpdatedAt`/`UpdatedBy` changed, versions and passages untouched; the list excludes it by default and `status=archived` includes it; a repeated archive gives `409 documents.already_archived`.
- **AC-16** Given an archived document, then the same content can be imported as a new document (`202`), and no retrieval-eligibility flag (`Status = Active`) holds for the archived one.
- **AC-17** Given an archived document, when `POST …/restore`, then `200`, `status` is derived from its latest version, and it is retrievable again with its active version; given an active document, then `409 documents.not_archived`.
- **AC-18** Given an archived document and another active document with the same latest content, when restore, then `409 documents.duplicate_content` with `existingDocumentId`, and the document stays archived.
- **AC-19** Given a run active when the document is archived, then the run still completes, the version is `Indexed`, `ActiveVersionId` is set and the document is still `Archived`.

Authorization and data

- **AC-20** Given an anonymous caller, then all five endpoints return `401`; given a KnowledgeSeeker, then `403`; given a ContentOwner and a Maintainer, then success.
- **AC-21** Given the migration applied to a database with existing documents, then `documents.revision` exists, is `not null`, and existing rows have `1`.
- **AC-22** Given each action, then the counters `documents.versions.created` (tag `origin`), `documents.metadata.updated`, `documents.archived`, `documents.restored` and `documents.lifecycle.rejected` (tag `reason`) change as expected (`MetricCollector<long>`).

UI (bUnit / Web tests)

- **AC-23** Given an owner, then the actions menu lists Upload new version, Reprocess and Archive (Restore when archived); given a seeker, then none of them and no Edit button; unavailable actions are disabled with a tooltip per the state rules.
- **AC-24** Given Edit, then the panel shows a form with the import's field rules; Save sends `PUT` with `If-Match` from the last ETag, shows the saving state, then the saved values; Cancel discards changes; field errors appear under their inputs.
- **AC-25** Given a `412`, then the conflict banner appears with Reload and Keep editing; Reload shows the server values and a fresh ETag; Keep editing preserves the input.
- **AC-26** Given the new-version dialog, then only the file picker is shown with client-side checks; on `202` it closes and the detail page shows the new version in `IngestionStatus` and opens the progress stream; each 409 variant shows its specific message (duplicate links to the existing document).
- **AC-27** Given Reprocess, Archive or Restore, then a `ConfirmDialog` is required first; cancelling sends nothing; confirming calls the endpoint and refreshes the page; restore's duplicate conflict shows the link.
- **AC-28** Given the History tab after actions, then new versions appear with origin Upload or Reprocess and their runs.

Architecture

- **AC-29** The Documents project still references only `RagAssistant.Shared`; the migration is applied by `DatabaseMigrator` and by the MigrationService; `arch-check` passes.

## Testing Approach

- Integration tests (TUnit, Testcontainers, `RagAssistantAppFactory`) for AC-1 to AC-22. Tests that need indexing use the dedicated ingestion factory with the controllable fake generator introduced in spec 007; tests that do not need it use the default factory, where runs stay queued. State is seeded through `DocumentsDbContext` when a scenario needs an indexed or failed version.
- Concurrency tests (AC-3, AC-11) run two requests in parallel against the same document and assert exactly one success.
- bUnit and `StubHost` Web tests for AC-23 to AC-28.
- Manual: run the AppHost, edit a title, upload a revised file, watch it supersede, reprocess, archive and restore; record in Implementation Notes.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Pruning passages/embeddings of old versions | Keep all versions (ADR 0004; fine at ~100 documents) | Storage or HNSW size becomes a problem |
| Cancelling a running ingestion | Let it finish (also when archived) | Long runs or costly providers |
| Lifecycle audit table (who changed what, before/after) | `UpdatedAt` / `UpdatedBy` plus the versions list | Compliance needs an audit trail |
| Draft/publish step before activation | Auto-activate on indexing (spec 006) | Content needs review before going live |
| `If-Match: *` and weak ETags | Not supported | A client needs them |
| Hard delete, purge | Archive only | Retention or GDPR |
| Row actions in the list, bulk archive | Detail page only | Bulk curation becomes common |
| Metadata edit history | None | Same as audit table |

## Open Questions

None. Resolved in review (2026-10-14): a missing `If-Match` is `428`; content equal to an older, non-latest version is accepted as a rollback version.
