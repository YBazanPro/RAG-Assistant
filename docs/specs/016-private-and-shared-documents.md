# Spec 016: Private and Shared Documents

**Status:** Implemented (2026-10-16). Approved 2026-10-16. Differences from the approved text: the unique index on the active content hash is now per owner (`latest_content_hash, created_by`) because a global one would stop another user importing a file that someone else holds privately (the handlers' duplicate guard, which looks only at readable documents, covers the rest); `updatedBy` on the detail, and `createdBy` of a version, are also returned to the person named in them, not only to the document's owner; the browser smoke tests (AC-25 upload with a real PDF, AC-27 dialog focus) are not written yet, bUnit and integration tests cover the rest
**Date:** 2026-10-16
**Roadmap:** new step 3.8 ([roadmap](../roadmap.md)) · **Decision record:** [ADR 0009](../adr/0009-document-visibility.md) (accepted 2026-10-16) · **Builds on:** specs [006](006-documents-data-model-and-import.md) (import), [008](008-browse-corpus-and-documents-ui.md) (browse, UI), [009](009-document-lifecycle.md) (metadata edit, `If-Match`), [010](010-pdf-import.md) (original file), [011](011-retrieval.md) (`IDocumentSearch`), [012](012-chat-conversations-and-grounded-answers.md) (citation snapshots), [015](015-chat-scope-activity-and-layout.md) (scope, activity, `IDocumentCatalog`)

## Problem

Every signed-in user can see and search every document. A content owner who wants to import a draft or personal material for their own questions cannot do it without exposing it to colleagues, and anything they import shows up in everyone's answers. ADR 0009 chose the smallest step: a per-document `Visibility` of `Shared` or `Private`, owner-only access to private content, no tenants. This spec turns that into behavior, with the rule enforced in one place (the Documents module) on every read path.

## Scope

### In

- `Visibility` (`Shared` | `Private`) on `Document`, one migration, existing rows become `Shared`.
- Choice on import and in the metadata edit; badge in the document list and detail.
- Enforcement on every Documents read path and on write actions that name a document.
- Contract change: callers of `IDocumentSearch` and `IDocumentCatalog` pass the viewer, so the rule is applied inside the module and cannot be skipped.
- Interplay with spec 015 (scope, activity, "Document unavailable") and with answers already given.
- Tests with two users per path, `rag-pipeline` skill and CLAUDE.md updates.

### Out

- Tenants, groups, per-user sharing lists, transfer of ownership (ADR 0009).
- Maintainer view of private documents' metadata in the admin area: the admin area is Phase 4 and does not exist yet. Until it does, maintainers see no private documents anywhere (see Deferred Decisions).
- Changing visibility per version: it is a property of the document.
- Encryption at rest or any protection against someone with database or file-store access.
- Bulk visibility change.

## Domain Model

### Visibility and owner

- `Document.Visibility`: enum `Shared = 0`, `Private = 1`, stored as text like the other enums in the module, `NOT NULL`, default `Shared` in the migration so existing rows become `Shared`.
- The **owner** is `Document.CreatedBy`, the `sub` recorded at import (never changes, never an email or username). It is *not* `OwnerName`, the free-text display field of spec 006; the UI keeps calling that field "Owner" and the new rule "Visibility", and the detail page shows the access owner as "Imported by" only to the owner.
- Index `(Visibility, CreatedBy)` on `documents.Documents`.

### The one rule

A user `U` can **read** document `D` when `D.Visibility = Shared OR D.CreatedBy = U.sub`. Everything below is this predicate. It lives in one internal helper in the Documents module (an `IQueryable<Document>` extension, `ReadableBy(viewer)`), used by every query that starts from `Documents`, `DocumentVersions` or `Passages`; queries on versions and passages reach the rule through their document, never through a copy of the flag on the passage (no denormalised flag that can go stale).

A user can **write** to D (edit, new version, reprocess, archive, restore, retry, watch ingestion events) when they can read it and have the `ContentOwner` policy, exactly as today. Nothing else about writes changes, except the two rules in "Switching" below.

### Switching

- `Visibility` is part of the metadata edit (`PUT /api/documents/{id}`, spec 009). It bumps `Revision` and `UpdatedBy/At` like any metadata change, and the `If-Match` check applies.
- **Only the owner may change `Visibility`**, in either direction. A different `ContentOwner` who can read a `Shared` document and sends a changed `visibility` gets `403` `documents.visibility_owner_only`; sending the unchanged value is fine, so a client that PUTs the whole body keeps working.
- Shared → Private takes effect at once: the next search or read by anyone else excludes the document. Nothing is re-embedded or re-chunked; the visibility lives on the document row only.
- Private → Shared exposes the current active version at once (it is already indexed).
- Archiving, restoring and versioning keep the visibility.

### Import

`POST /api/documents` (multipart, text and PDF) gains an optional field `visibility` (`shared`/`private`, case-insensitive); missing means `Shared`. `CreatedBy` becomes the owner. Invalid value: `400` through the existing validation shape. `UploadVersion` does not take it.

### Duplicate guard

The duplicate-content check (`documents.duplicate_content`, spec 006, also used by restore and new-version) compares only against documents the caller **can read**. Otherwise a `409` that names the existing document would reveal that someone else holds a private copy. A caller importing the same file as another user's private document gets a new document. Restoring a document whose hash now matches another readable active document keeps today's conflict.

### Citation snapshots and past answers

Unchanged from ADR 0004 and the ADR: a snapshot (passage text, title, version, section, pages) was authorised when the answer was given and stays readable by the conversation's owner, whatever the document becomes later. Making a document private cascades nothing in Chat. The consequence the UI must respect: for a cited document that the viewer can no longer read, the snapshot is the only source, and the "Open in document" and "Open page" links (original file) are not shown, because they would only produce a 404.

## Contract changes (SharedKernel)

Callers pass the viewer explicitly. No ambient `HttpContext` lookup inside the module, because the answer pipeline also runs from retries and the evaluation harness.

```csharp
Task<Result<SearchResult>> SearchAsync(string query, SearchFilters filters, int topK, string viewerSub, CancellationToken ct);
Task<DocumentAvailability> CheckAsync(IReadOnlyCollection<Guid> documentIds, string viewerSub, CancellationToken ct);
```

- `viewerSub` is required (non-empty, validated; empty gives `validation.failed`). There is no overload without it and no "system" value, so a caller cannot opt out.
- `IDocumentSearch`: the visibility predicate is part of the same SQL query as the language, tag, document-id and active-version filters, so the HNSW search, `topK` and the evidence thresholds apply to readable passages only. A private passage of someone else never enters the candidate set, the near-duplicate reduction, the thresholds or the result.
- `IDocumentCatalog`: ids the viewer cannot read are treated exactly like ids that do not exist: absent from `Known`, `Searchable` and `Titles`. There is no signal that tells "private" from "missing".
- The Chat module passes the **conversation owner's** `sub`: for a normal question, for a follow-up, and for a retry (a retry by the same user, always the owner). The pipeline never uses a maintainer's or a system identity.
- The evaluation harness (Phase 5) runs under its own principal and reads the sample corpus, which is `Shared`; the seeded sample corpus and the test `CorpusSeeder` default to `Shared`.

## API Contract

No new routes. Changes to existing ones, all under `/api/documents` (policy as today unless noted):

| Route | Change |
|---|---|
| `POST /api/documents` | accepts `visibility`; response and `GET` gain `visibility`. |
| `GET /api/documents` | returns only readable documents; each row has `visibility` and `isOwner`. `total`, paging and sorting count readable rows only. New optional filter `visibility=shared\|private\|mine` (`mine` = private documents of the caller, i.e. `Private` and owned; default none). |
| `GET /api/documents/facets` | owners, languages and tags (and their counts) are computed over readable documents only. |
| `GET /api/documents/{id}` | `visibility`, `isOwner`; `404` when not readable. `createdBy` (the raw `sub`) is returned only to the owner; for others it is omitted on shared documents too (the UI never needed it, and it is the access identity). |
| `PUT /api/documents/{id}` | body gains `visibility`; rules in "Switching". |
| `GET …/{id}/versions`, `GET …/versions/{n}/passages`, `GET …/versions/{n}/file` | `404` when the document is not readable. `createdBy` on a version is hidden from non-owners, as above. |
| `POST …/archive`, `…/restore`, `…/reprocess`, `…/versions`, `…/ingestion/retry`, `GET …/ingestion/events` | `404` when not readable. |
| `GET /api/documents/search` (diagnostic, `Maintainer`) | runs through the same pipeline with the caller's `sub`: a maintainer finds shared documents and their own private ones, nothing else. |

Not readable is always `404` (never `403`), with the same ProblemDetails as an unknown id, and a response body and timing that do not depend on whether the document exists. The only `403` is `documents.visibility_owner_only`, which is returned to a user who can already read the document.

Chat endpoints keep their shapes. `GET /api/chat/conversations/{id}` and the activity endpoint resolve titles through the catalog (above), so an unreadable scope document has `title: null, available: false` (the UI already renders "Document" / "unavailable"), and an unreadable passage in a trace has `documentTitle: null`.

`PUT /api/chat/conversations/{id}/scope` reports an unreadable id exactly like an unknown id (`400` listing it), so the response cannot be used to probe for private documents.

## Authorization

| Who | Reads | Writes |
|---|---|---|
| Owner of a private document | content, metadata, file, all versions | everything a `ContentOwner` may do; may switch visibility |
| Any other user, including `Maintainer` | nothing; the document does not exist for them | nothing |
| Any user with the right role, on a `Shared` document | as today | as today; may not change `Visibility` unless owner |

`Maintainer` gets no bypass in this spec. Tokens carry no new claim and Keycloak is not touched (no change to the realm file).

## UI (Web.Client)

- **Import form** (`DocumentUpload`): a "Visibility" choice, two radio options with one-line help: *Shared* ("Everyone who signs in can search and read it") and *Private* ("Only you. It is never used to answer anyone else's questions"). Default Shared. Sent as `visibility` in the multipart body (the file still goes through `upload.js`; add the field to its form data and to `IFileUploader`).
- **Metadata panel** (`DocumentMetadataPanel`): the same choice, editable by the owner only; for another `ContentOwner` it is read-only text with a hint. Switching to Private asks for a short confirmation ("Other people will no longer find this document or get answers from it. Answers they already received keep their citations."), switching to Shared too ("Everyone who signs in will be able to search and read it."). The existing `412`/conflict handling of the panel covers a concurrent edit.
- **Document list** (`DocumentTable`): a visible badge "Private" (lock icon plus text, never colour alone) next to the title for private documents; shared documents show no badge to keep the list quiet. The filter bar gets "Visibility: All / Mine private / Shared". The detail header shows the same badge.
- **Chat** (`ScopeBar`, `ScopePicker`, `ActivityPanel`, `SourcePreview`): the picker lists only what `GET /api/documents` returns (readable), with the badge. A scope document that became unreadable shows "Document unavailable" with no title; the activity lists it under "Left out" like an archived one. For non-cited passages of an unreadable document the activity shows "Document unavailable". For cited passages the snapshot title stays and the preview works from the snapshot, with the document links hidden when `GET /api/documents/{id}` would 404 (the answer's citation view carries `documentReadable`, computed through the catalog).
- Copy is plain English, no localization (spec 005).

## Edge Cases & Failure Modes

- **Owner makes a document private while others' conversations use it in scope.** Their next question excludes it; the scope chip shows "Document unavailable"; if nothing readable is left in their document scope, the answer is the usual refusal (no wider fallback, spec 015 AC-7).
- **Two edits race** (owner switches while another ContentOwner edits tags): `If-Match` decides; the loser gets the existing conflict response.
- **A question in flight** when visibility flips: it finishes with the passages it already retrieved (retrieval is one query); later questions see the new state. Citations of that answer are snapshotted as usual.
- **Retry of an old answer** after the document turned private: the retry re-runs retrieval for the conversation owner and so excludes it. The original answer and its snapshots stay as they were.
- **Ingestion** (worker, retries, SSE progress) is a system process and is not subject to the rule; only the endpoints that expose it are.
- **Original files** are content-addressed (`pdf/<sha256>.pdf`) and shared between documents with identical content. Access goes only through `GET …/versions/{n}/file`, which checks the document; the store is never exposed by hash.
- **Owner leaves the organisation or loses a role.** The owner's private documents stay private and unreachable by others; a `ContentOwner` role loss does not remove the right to *read* the user's own private documents through `KnowledgeSeeker`. Recovery is a database operation (see Deferred Decisions).
- **User A and B import the same file, A private.** Both succeed (duplicate guard sees only readable documents). If A later switches to Shared, two active documents with the same hash exist; this is allowed and not merged. The switch does not run the duplicate guard.
- **Facets and counts** never include unreadable documents, so tag or owner names of private documents cannot be inferred. `GET /api/documents` sort/paging uses the filtered set.
- **Timing and errors** for unreadable ids match unknown ids; log lines at Information do not name private titles beyond the existing document id.
- **Traces** hold titles of retrieved passages (spec 012). They are readable only by the conversation owner (activity endpoint) and are returned with titles of now-unreadable, non-cited documents blanked.
- **Sample corpus, seeders, evaluation harness** write `Shared` explicitly.
- **Migration** is additive and idempotent; no data movement, no downtime. Rolling back means dropping the column and index.

## Non-Functional Requirements

- The visibility predicate adds no more than 10 ms to retrieval on the dev corpus, and the HNSW search stays in use. If a selective predicate makes the planner skip the index the result is still correct, only slower (record in the spec 011 notes if observed).
- One predicate implementation; a test (architecture style, reflection over registered endpoints) fails if a query in the Documents module reads `Documents`, `DocumentVersions` or `Passages` for a request path without it (see AC-24).
- No change to module boundaries: Chat still reaches Documents only through the two contracts; no Documents type enters SharedKernel.
- Accessibility: the visibility radio group has a legend and per-option help text associated with the input; the badge has text, and the confirmation is a dialog with a focus trap, as the other dialogs.

## Integrations

None. Keycloak, the YARP BFF and the realm file are unchanged: the BFF already forwards the user's access token and the API reads `sub` from it. The Web project links no new kernel code.

## Acceptance Criteria

Personas below are two distinct users, "owner" (`sub` A) and "other" (`sub` B); tests use `CreateClientAs(persona, subject:)` to give each a stable `sub`, with one case per path using `Maintainer` as "other".

**Model and import**
- **AC-1** Given the migration runs on a database with documents, then every existing document is `Shared` and the index `(Visibility, CreatedBy)` exists.
- **AC-2** Given an import without `visibility`, then the document is `Shared`. Given `visibility=private` it is `Private` and owned by the caller's `sub`. Given `visibility=secret` the response is `400`.
- **AC-3** Given owner imports a PDF as private, then the original file is stored and extraction works exactly as for a shared one (ingestion is unaffected).
- **AC-4** Given owner has a private document, when other imports the same content, then it succeeds with a new document. Given the same content already exists as a *shared* active document, other still gets `documents.duplicate_content`.

**Reads (each with owner and other)**
- **AC-5** `GET /api/documents`: owner sees their private document with `visibility: private, isOwner: true`; other does not see it and the total excludes it; `visibility=mine` returns only owner's private documents.
- **AC-6** `GET /api/documents/facets`: a tag and an owner name that exist only on a private document appear for owner and not for other.
- **AC-7** `GET /api/documents/{id}`: owner `200`; other `404` with the same body as a random id. A shared document returns `200` to both, and `createdBy` appears only for the document's owner.
- **AC-8** `GET …/versions`, `…/versions/{n}/passages` and `…/versions/{n}/file`: owner `200`; other `404`; maintainer `404`.
- **AC-9** Write actions (`PUT`, archive, restore, reprocess, new version, ingestion retry, ingestion events): owner as today; other `404`; the document is unchanged afterwards.
- **AC-10** `GET /api/documents/search` as a maintainer: finds a shared document and the maintainer's own private one, never another user's private one.

**Retrieval and the contracts**
- **AC-11** `IDocumentSearch` with `viewerSub` = owner returns passages of the owner's private document; with `viewerSub` = other it never returns them, whatever the query, filters (`DocumentIds` naming it, tags, language) or `topK`.
- **AC-12** Given a private document is the only strong match, when other asks about it, then the outcome is `NoEvidence` (refusal, no model call), not `LowConfidence`.
- **AC-13** Given private passages are near-duplicates of shared ones, then they neither displace nor suppress shared results for other (reduction and thresholds see readable candidates only).
- **AC-14** `IDocumentCatalog.CheckAsync` with an unreadable id returns the same result as for a random id: not in `Known`, `Searchable` or `Titles`.
- **AC-15** Both contract methods reject an empty `viewerSub` (`validation.failed` for search).

**Switching**
- **AC-16** Given owner switches a shared document to private with the right `If-Match`, then `Revision` increments, other's next list/detail/search no longer shows it, and owner's still do.
- **AC-17** Given owner switches it back to shared, then other can find, read and search it again without re-ingestion.
- **AC-18** Given a different `ContentOwner` who can read a shared document sends a changed `visibility`, then the response is `403` `documents.visibility_owner_only` and nothing changes; sending the unchanged value succeeds.
- **AC-19** Given a stale `If-Match`, then the existing conflict response is returned and the visibility is unchanged.

**Chat interplay**
- **AC-20** Given other's conversation has the document in scope and owner makes it private, when other asks a question, then it is excluded, the scope view shows `title: null, available: false`, the activity lists it under "Left out", and, if it was the only scope document, the answer is a refusal with no model call.
- **AC-21** Given other has an answer that cited the document before it turned private, then the answer text, citations, snapshot passage text and source preview still load for other, and the citation view marks the document as not readable (links hidden). Given other retries that answer, the retry excludes the document.
- **AC-22** Given the activity of an answer, then a non-cited passage of a now-private document has `documentTitle: null` and the UI shows "Document unavailable", while a cited one keeps its snapshot title.
- **AC-23** Given `PUT …/scope` names owner's private document as other, then the response is `400` identical to naming a random id, and the scope is unchanged. As owner it succeeds and retrieval can use it.
- **AC-24** Given the Documents module's request-path queries, then a test fails for any registered endpoint that reaches documents, versions or passages without the visibility predicate (a registry of the endpoints and their helper use, kept next to the endpoint map test).

**UI**
- **AC-25** Given the import form, then Visibility is Shared by default, and choosing Private submits `visibility=private` (bUnit for the form; browser smoke test for the real upload with a PDF).
- **AC-26** Given the document list as owner, then a private document shows a "Private" badge with text and the filter "Mine private" narrows to it; as other it is absent.
- **AC-27** Given the metadata panel, then the owner can switch visibility after a confirmation dialog (focus trapped, focus returns to the opener); another `ContentOwner` sees it read-only.
- **AC-28** Given a scope or activity entry for an unreadable document, then the text "Document unavailable" is shown and no title; links to the document are hidden in the source preview while the snapshot text is shown.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Maintainer metadata view of private documents (ADR 0009: title, owner, status in admin) | Maintainers see no private documents anywhere. The admin spec (Phase 4) adds a dedicated, metadata-only listing; it must not reuse these read paths. | Closed by [spec 017](017-diagnose-traces-admin-and-metrics.md): `GET /api/admin/documents/private`, metadata only |
| Orphaned private documents (owner left) | Stay private and unreachable; fixed by a manual database operation (set `Visibility`) | the first real case, or a user-management story |
| Ownership transfer | not built | a user needs it |
| Showing a visibility audit trail | not built; `UpdatedBy/At` and `Revision` already record the change | compliance need |
| Per-conversation warning "your scope includes documents others can no longer read" | silent exclusion with "Document unavailable" | feedback from use |

## Open Questions

None. Settled at approval (2026-10-16), decisions the ADR did not make:

1. The owner is `CreatedBy` (the importing `sub`); `OwnerName` stays a free-text display field.
2. Callers pass the viewer explicitly as a required argument on both contracts (not an ambient HTTP context).
3. Unreadable is always `404`; only the owner may change visibility (`403` for other readers who try).
4. The duplicate guard counts only documents the caller can read.
5. `createdBy` is hidden from non-owners on shared documents.
6. Maintainers have no bypass until the Phase 4 admin spec.
