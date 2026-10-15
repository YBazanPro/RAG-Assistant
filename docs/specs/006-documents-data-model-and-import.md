# Spec 006: Documents module — data model and import

> **Naming note (2026-10-15):** the backend project `RagAssistant.Host` has since been renamed `RagAssistant.Api` (Aspire resource `api`, configuration keys `services:api:*`, test stub `StubApi`). "The Host" in this document means that project.

**Status:** Approved (2026-10-14)
**Date:** 2026-10-14
**Roadmap:** steps 1.1 + 1.2 ([roadmap](../roadmap.md)) · **Decision records:** [ADR 0003](../adr/0003-model-provider.md), [ADR 0004](../adr/0004-document-versioning.md)

## Problem

Content owners have no way to put knowledge into the system: there is no module, no table and no endpoint for documents, so nothing downstream (ingestion, retrieval, chat) can be built or demoed. The shape of the documents data (versions, stable passages, ingestion runs) is expensive to change once data exists (ADR 0004), and every later Phase 1 step (1.3 ingestion, 1.4 progress SSE, 1.5 browse, 1.7 lifecycle) builds on it. This spec therefore fixes the **complete Phase 1 schema** once, and delivers the first behaviour on top of it: a content owner imports a Markdown or plain-text document with metadata, and the system accepts it and queues it for ingestion.

## Scope

### In

- The `Documents` module (`RagAssistant.Modules.Documents`, scaffolded with `/scaffold`): `DocumentsDbContext` in schema `documents`, `AddDocumentsModule` + `AddDocumentsPersistence`, registration in the Host and the MigrationService, its own scoped Mediator.
- The full Phase 1 schema and its initial migration: `documents`, `document_versions`, `passages` (with `vector(1024)` + HNSW), `ingestion_runs`, `ingestion_events`; `HasPostgresExtension("vector")`, `UseVector()`.
- Options: `Documents:Import` (max file size) and `Documents:Embeddings` (model, dimensions), validated on startup.
- `POST /api/documents` — multipart import → `202 Accepted`.
- `GET /api/documents/{id}` — minimal detail (metadata, derived status, latest version), extended by 1.5.
- Metrics: `Meter("RagAssistant.Documents")` with import counters; structured logs.
- Integration tests (TUnit + Testcontainers) for every acceptance criterion, including 401/403 per persona.
- Docs: roadmap (1.1/1.2 done, new step **1.8 PDF import**), `product.md` first-release formats line pointing to 1.8, CLAUDE.md module table (Documents exists).

### Out

- Running ingestion: extraction, chunking, embedding, the `BackgroundService`/`Channel<T>`, Ollama in the AppHost, `IEmbeddingGenerator` registration (1.3). Passages and embeddings tables exist but stay empty.
- Ingestion progress SSE (1.4) — the event log is written, not read.
- List/filter/paging, passages endpoint (1.5); Documents UI (1.6).
- Edit metadata, new version upload, archive, reprocess, retry (1.7).
- PDF/DOCX (new roadmap step 1.8, own spec + ADR); hard delete (deferred).
- `IDocumentSearch` and any cross-module contract (2.1).

## Domain Model

All entities are `internal`, in schema `documents`. IDs are `Guid` (`Guid.CreateVersion7()`), except `ingestion_events.id`. Timestamps are `DateTimeOffset` from `TimeProvider`. Enums are stored as strings.

### Document — stable identity

| Field | Type | Rules |
|---|---|---|
| `Id` | Guid | Stable across versions; what citations and URLs reference |
| `Title` | text | Required, trimmed, 1–200 |
| `OwnerName` | text | Required, trimmed, 1–100 — organisational owner (e.g. "Finance"), not a user |
| `Language` | text | ISO 639-1 two-letter code, lowercase; default `en`; any valid code accepted |
| `Tags` | `text[]` | 0–20 items, each trimmed, lowercased, 1–50 chars, de-duplicated; GIN index |
| `EffectiveDate` | `date`, nullable | Informational and filterable; never affects eligibility |
| `Source` | text, nullable | ≤ 500, free text (URL or label); displayed only, never fetched |
| `Status` | enum | `Active` \| `Archived` (archive arrives in 1.7) |
| `ActiveVersionId` | Guid, nullable | FK → the indexed version currently retrievable; null until the first version is indexed |
| `LatestContentHash` | text | SHA-256 (hex) of the latest version's normalized content; partial unique index `WHERE status = 'Active'` (duplicate guard) |
| `CreatedAt` / `CreatedBy` | timestamptz / text | `CreatedBy` = caller's `sub` |
| `UpdatedAt` / `UpdatedBy` | timestamptz / text | `sub` |

### DocumentVersion — one uploaded (or reprocessed) content

| Field | Type | Rules |
|---|---|---|
| `Id` | Guid | |
| `DocumentId` | Guid | FK, required |
| `Number` | int | 1, 2, 3… per document; unique (`DocumentId`, `Number`) |
| `Origin` | enum | `Upload` \| `Reprocess` |
| `Status` | enum | `Queued` \| `Processing` \| `Indexed` \| `Failed` \| `Superseded` |
| `SourceFormat` | enum | `Markdown` \| `PlainText` (`Pdf` reserved for 1.8) |
| `OriginalFileName` | text | File name without path, ≤ 255 |
| `ContentType` | text | As declared by the client; informational only |
| `Content` | text | Normalized UTF-8 source (BOM stripped, LF line endings) |
| `ContentHash` | text | SHA-256 hex of `Content` |
| `SizeBytes` | int | UTF-8 byte length of the uploaded file |
| `EmbeddingModel` / `EmbeddingDimensions` | text / int, nullable | Set by the embed stage (1.3) |
| `PassageCount` | int, nullable | Set when indexed |
| `CreatedAt` / `CreatedBy` | timestamptz / text | |
| `IndexedAt` | timestamptz, nullable | |

### Passage — retrievable unit (written by 1.3)

| Field | Type | Rules |
|---|---|---|
| `Id` | Guid | New per version, never reused (ADR 0004) |
| `VersionId` | Guid | FK, required |
| `Position` | int | 0-based order within the version; unique (`VersionId`, `Position`) |
| `SectionPath` | `text[]` | Heading chain, e.g. `{Budget, Exceptions}`; empty for no headings |
| `SectionOrdinal` | int | Order of the passage within its section |
| `Text` | text | Passage content |
| `CharCount` / `TokenEstimate` | int | |
| `PageStart` / `PageEnd` | int, nullable | Always null for text formats (PDF-ready) |
| `Embedding` | `vector(1024)`, nullable | HNSW index, `vector_cosine_ops`, default parameters |

The "parent section" of a passage is the set of passages of the same version with an equal `SectionPath`. Passages are immutable once their version is `Indexed`.

### IngestionRun — one attempt to ingest a version

| Field | Type | Rules |
|---|---|---|
| `Id` | Guid | |
| `VersionId` | Guid | FK |
| `Attempt` | int | 1, 2… per version; unique (`VersionId`, `Attempt`) |
| `Status` | enum | `Queued` \| `Running` \| `Succeeded` \| `Failed` |
| `CurrentStage` | enum | `Queued` \| `Extracting` \| `Chunking` \| `Embedding` \| `Storing` \| `Completed` |
| `QueuedAt` / `StartedAt` / `FinishedAt` | timestamptz (last two nullable) | |
| `ErrorCode` / `ErrorMessage` | text, nullable | |

Index on `Status` (startup re-queue in 1.3).

### IngestionEvent — append-only stage log

| Field | Type | Rules |
|---|---|---|
| `Id` | bigint identity | Monotonic; becomes the SSE event id in 1.4 |
| `RunId` | Guid | FK; index (`RunId`, `Id`) |
| `Stage` | enum | As `CurrentStage` |
| `Status` | enum | `Started` \| `Completed` \| `Failed` |
| `Message` | text, nullable | Never document content |
| `OccurredAt` | timestamptz | |

### Lifecycle (whole Phase 1; this spec implements only the first transition)

```text
Import ──► Version Queued + Run Queued + Event(Queued, Completed)        ◄── this spec
1.3:  Run Running → stages … → Succeeded ⇒ Version Indexed,
      Document.ActiveVersionId = version, previous active version ⇒ Superseded
      Run Failed ⇒ Version Failed (the previous active version stays active)
1.7:  new upload / reprocess ⇒ new Version (Number+1) + new Run; archive ⇒ Document Archived
```

**Derived display status** (returned by the API, not stored): `Archived` if the document is archived; otherwise the status of the latest version if it is `Queued`, `Processing` or `Failed`; otherwise `Active`.

**Retrievable** (consumed by 2.x): `Document.Status = Active` and the passage's version is `Document.ActiveVersionId`.

## API Contract

All routes are under the `/api` group (spec 002) and return ProblemDetails with `code` and `traceId` on error.

| Verb | Route | Request | Success | Errors |
|---|---|---|---|---|
| POST | `/api/documents` | `multipart/form-data`: `file` (required), `title`, `ownerName`, `language?`, `tags?` (repeated field), `effectiveDate?` (`yyyy-MM-dd`), `source?` | `202 Accepted`, `Location: /api/documents/{documentId}`, body `ImportDocument.Response` | 400 validation · 401 · 403 · 409 duplicate · 413 body too large |
| GET | `/api/documents/{documentId:guid}` | — | `200` `GetDocument.Response` | 401 · 404 |

```jsonc
// 202 — ImportDocument.Response
{ "documentId": "…", "versionId": "…", "versionNumber": 1, "ingestionRunId": "…", "status": "queued" }

// 200 — GetDocument.Response
{
  "id": "…", "title": "Budget Exception Policy", "ownerName": "Finance", "language": "en",
  "tags": ["budget", "finance"], "effectiveDate": "2026-01-01", "source": null,
  "status": "queued",            // derived display status
  "activeVersionId": null,
  "latestVersion": { "id": "…", "number": 1, "status": "queued", "sourceFormat": "markdown",
                     "originalFileName": "budget.md", "sizeBytes": 18234, "createdAt": "…" },
  "createdAt": "…", "createdBy": "<sub>", "updatedAt": "…", "updatedBy": "<sub>"
}

// 409 — duplicate
{ "status": 409, "code": "documents.duplicate_content", "detail": "…", "existingDocumentId": "…", "traceId": "…" }
```

Validation errors: file-rule failures return `400` with a top-level `code` (`documents.file_required`, `documents.unsupported_extension`, `documents.file_too_large`, `documents.invalid_utf8`, `documents.empty_content`) and the message under `errors.file`. Metadata failures (`title`, `ownerName`, `language`, `tags`, `effectiveDate`, `source`) use the shared `validation.failed` code with FluentValidation messages under their field names.

Notes:
- The format comes from the extension: `.md`/`.markdown` → Markdown, `.txt` → PlainText (case-insensitive). The client's content type is not trusted.
- The Host endpoint uses `.DisableAntiforgery()`. CSRF protection is the BFF's `X-Requested-With` requirement (spec 005), and the Host only accepts bearer tokens.
- The `409` needs an `existingDocumentId` ProblemDetails extension. The shared `Error`/`ToProblem` mapping gains optional extension values. This is a small shared-kernel change with no business logic.
- No idempotency key: a client retry of the same content gets `409` with the existing id.

## Authorization

| Endpoint | Policy | Knowledge Seeker | Content Owner | Maintainer | Anonymous |
|---|---|---|---|---|---|
| `POST /api/documents` | `ContentOwner` | 403 | 202 | 202 | 401 |
| `GET /api/documents/{id}` | `KnowledgeSeeker` | 200 (any status, incl. queued/failed/archived) | 200 | 200 | 401 |

- Any ContentOwner may manage any document; there is no per-document ownership check (ADR 0002, no per-corpus rules).
- `CreatedBy` / `UpdatedBy` hold the `sub` claim, never a username or email.

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| No `file` part, or more than one file | 400 `documents.file_required` (only one file per import) |
| Extension not `.md`/`.markdown`/`.txt` (incl. no extension) | 400 `documents.unsupported_extension` |
| File > `Documents:Import:MaxFileSizeBytes` (default 1 048 576) | 400 `documents.file_too_large`. A whole request over the Kestrel/form limit (max file size + 64 KB for fields) gets 413 ProblemDetails |
| Invalid UTF-8 bytes | 400 `documents.invalid_utf8` (strict decoder, no transcoding) |
| Empty or whitespace-only content (after BOM strip) | 400 `documents.empty_content` |
| UTF-8 BOM, CRLF or CR line endings | BOM removed, line endings normalized to LF; the hash is computed on the normalized text |
| File name contains a path (`C:\x\a.md`, `../a.md`) | Only the final segment is stored |
| Tags with mixed case, whitespace or duplicates | Normalized; an empty tag after trimming → 400 |
| Invalid language (`english`, `EN-us`, `xx1`) | 400; `EN` is accepted and lowercased; valid = two ASCII letters |
| Same normalized content as the latest version of an **active** document | 409 `documents.duplicate_content` + `existingDocumentId` |
| Same content as an **archived** document | Accepted (new document) |
| Two identical imports at the same moment | One gets 202, the other 409. The partial unique index on `LatestContentHash` is mapped from the unique violation (Npgsql `23505` on that index) to the same 409 |
| Same title as an existing document | Accepted; titles are not unique |
| Client disconnects mid-upload | Request cancelled through the `CancellationToken`; nothing persisted (single transaction) |
| DB failure during the transaction | 500 ProblemDetails via the global handler; nothing partially persisted |
| `GET` with an unknown id | 404 `documents.not_found` |
| Imported document before 1.3 ships | Stays `queued` indefinitely (accepted: no worker yet) |

## Non-Functional Requirements

- **Latency:** import of a 1 MB file < 500 ms p95 on the dev machine (one transaction, no AI call). `GET` < 100 ms p95.
- **Volume:** designed for ADR 0004 targets (~100 documents, ~10k passages). Default HNSW settings.
- **Memory:** the file is read through `IFormFile.OpenReadStream()` with the size checked first, never larger than the configured limit.
- **Configuration:** `Documents:Import:MaxFileSizeBytes` (default 1 048 576, > 0). `Documents:Embeddings:Model` (default `bge-m3`) and `Documents:Embeddings:Dimensions` (default 1024) must equal the migration's `vector(1024)`: startup validation fails fast on a mismatch. Bound with `ValidateOnStart`.
- **Logging:** source-generated `LoggerMessage` entries with document id, version id, size, format and outcome. Never the content, title-only at Debug.
- **Metrics:** `Meter("RagAssistant.Documents")` with the counters `documents.imported` (tag `format`) and `documents.import.rejected` (tag `reason` = validation code or `duplicate`), registered with OpenTelemetry via ServiceDefaults or the module.
- **Architecture:** no reference from Documents to any other module, the Host only references `AddDocumentsModule`, and `arch-check` passes.

## Integrations

- **PostgreSQL / pgvector:** new schema `documents`, its own `__EFMigrationsHistory`, the `vector` extension. The migration is applied by the MigrationService (AppHost) and `DatabaseMigrator` (tests).
- **BFF (spec 005):** no change. `/api/documents` passes through YARP and the `X-Requested-With` header is required for POST. The BFF's request size limits (default ~28.6 MB) are above 1 MB.
- **OpenTelemetry:** new meter (above); traces come from ASP.NET Core and Npgsql instrumentation.
- **AI providers:** none in this spec (1.3).
- **Other modules:** none. No contract in `Shared/Contracts` yet.

## Acceptance Criteria

Import

- **AC-1** Given a ContentOwner and a valid `.md` file with title and owner, when they `POST /api/documents`, then the response is `202` with `documentId`, `versionId`, `versionNumber = 1`, `ingestionRunId`, `status = "queued"` and `Location: /api/documents/{documentId}`.
- **AC-2** Given AC-1, then the database holds one Document (`Active`, `ActiveVersionId = null`, `CreatedBy = sub`), one Version (`Queued`, `Number = 1`, `Origin = Upload`, `SourceFormat = Markdown`), one Run (`Queued`, `Attempt = 1`, `CurrentStage = Queued`) and one IngestionEvent (`Queued`/`Completed`).
- **AC-3** Given a `.txt` file, when imported, then `SourceFormat = PlainText`; given `.MARKDOWN`, then `Markdown`.
- **AC-4** Given content with a UTF-8 BOM and CRLF line endings, when imported, then the stored content has no BOM and only LF, and `ContentHash` is the SHA-256 of the normalized content.
- **AC-5** Given optional metadata omitted, when imported, then `language = "en"`, `tags = []`, `effectiveDate = null` and `source = null`.
- **AC-6** Given tags `[" Budget ", "budget", "FINANCE"]` and language `EN`, when imported, then the tags are `["budget", "finance"]` and the language is `en`.
- **AC-7** Given a file name `../../etc/policy.md`, when imported, then `OriginalFileName = "policy.md"`.

Validation

- **AC-8** Given each of: no file, an `.pdf` file, an empty file, a whitespace-only file, invalid UTF-8 bytes, a file of max size + 1 byte, then the response is `400` with the matching top-level `documents.*` code and a message under `errors.file`, and nothing is persisted.
- **AC-9** Given a missing or > 200-character title, a missing or > 100-character owner name, an invalid language, 21 tags, a 51-character tag, an unparsable effective date, or a > 500-character source, then `400` with an error for that field.
- **AC-10** Given a file of exactly `MaxFileSizeBytes`, when imported, then `202`.

Duplicates

- **AC-11** Given an active document whose latest version has content C, when content C (even with different line endings or a BOM) is imported again, then `409` with `code = documents.duplicate_content` and `existingDocumentId`, and nothing is persisted.
- **AC-12** Given two concurrent imports of identical new content, then exactly one returns `202` and the other `409`.
- **AC-13** Given an existing document with the same title but different content, when imported, then `202`.

Read

- **AC-14** Given an imported document, when a KnowledgeSeeker calls `GET /api/documents/{id}`, then `200` with its metadata, `status = "queued"`, `activeVersionId = null` and `latestVersion.number = 1`.
- **AC-15** Given an unknown id, when `GET`, then `404` with `code = documents.not_found`.

Authorization

- **AC-16** Given an anonymous caller, then both endpoints return `401`; given a KnowledgeSeeker, then `POST` returns `403`; given a Maintainer, then `POST` returns `202`.

Schema & configuration

- **AC-17** Given the migrations applied by `DatabaseMigrator`, then schema `documents` contains the five tables, the `vector` extension exists, `passages.embedding` is `vector(1024)` with an HNSW cosine index, and `documents.tags` has a GIN index.
- **AC-18** Given `Documents:Embeddings:Dimensions` ≠ 1024, when the Host starts, then startup fails with an options validation error.
- **AC-19** Given a successful import and a rejected one, then `documents.imported` and `documents.import.rejected` are each incremented once (verified with `MetricCollector<long>`).

Architecture

- **AC-20** The Documents project references only `RagAssistant.Shared` (plus packages); the Host and MigrationService register it; no other module references it.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| PDF (and DOCX) import, up to 100 MB | Text formats only, 1 MB; schema has `SourceFormat` and nullable page fields | Roadmap step 1.8: spec + ADR (PdfPig, binary storage outside Postgres, upload streaming, targets) |
| Draft/publish step before activation | Auto-activate on successful indexing | Content owners need review before content goes live |
| Effective-from/to windows affecting retrieval | Single informational `EffectiveDate` | Policies with expiry dates appear in the corpus |
| Hard delete | Archive only (1.7) | Retention or GDPR requires purging |
| Non-English documents | Any ISO 639-1 code accepted, UI/model English only | Evaluation shows a need, or multilingual UI returns |
| Tag catalogue / renaming | Free `text[]` tags | Tag sprawl becomes a problem |
| Per-document ownership/authorization | Any ContentOwner manages any document | A second audience or restricted content (ADR 0002) |

## Open Questions

None.

## Implementation Notes

Choices made while implementing, none of which change the approved behavior (recorded 2026-10-14):

- **Connection string:** `AddDocumentsPersistence` reads `ConnectionStrings:ragassistant` and lets EF create its own Npgsql data source with `UseVector()`. Using the Aspire-registered `NpgsqlDataSource` would have required `UseVector()` on it in both the Host and the MigrationService (and a `Pgvector` reference in each). The Host's `AddNpgsqlDataSource` stays for health checks and for tests that query the database directly.
- **Design-time factory:** `DocumentsDbContextFactory` lets `dotnet ef` build the model without starting the Host (which needs the Aspire connection string). `Persistence/Migrations/.editorconfig` marks the scaffolded migrations as generated code so analyzers and the format check skip them.
- **Mediator options:** the module sets `GenerateTypesAsInternal = true` and `Assemblies = [typeof(DocumentsModule)]`; without them the module and the integration tests' own Mediator collide (see [ADR 0005](../adr/0005-api-kernel-libraries.md)).
- **Form reading:** the endpoint reads the multipart form itself instead of binding it, because the request-size limit (`MaxFileSizeBytes` + 64 KB) must be set before the body is read, and binding reads it first. A malformed multipart body is reported like a missing file. The 413 path (`request.too_large`) already exists in the global exception handler; Kestrel enforces the limit, `TestServer` does not, so it is not covered by an integration test and was not run by hand.
- **Shared kernel additions:** `Error.Extensions` (extra ProblemDetails members, used for `existingDocumentId`), a `Result.Failure<T>(error, fieldErrors)` overload (a file-rule failure with its own `code` and a message under `errors.file`), and `ClaimsPrincipal.GetSubject()` instead of an `ICurrentUser` abstraction.
- **Order of checks:** metadata validation (FluentValidation, `validation.failed`) runs before the file rules, so a request with both problems reports the metadata first, and a non-multipart request (no form fields) is rejected for its missing title and owner name.
- **Metrics:** `documents.imported` is counted in the handler and `documents.import.rejected` in the endpoint (tag `reason` = the error code, so validation failures count too). ServiceDefaults subscribes to `AddMeter("RagAssistant.*")` so no module needs its own OpenTelemetry wiring.
- **Truncation:** an over-long original file name or content type (informational fields, 255 and 200 characters) is truncated rather than rejected.
- **Packages added:** `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Pgvector.EntityFrameworkCore` 0.3.0 and `Microsoft.EntityFrameworkCore.Design` 10.0.12 (Host, build-time only), all already named in CLAUDE.md, so no new ADR.
- **Tests:** 72 unit tests in `tests/Modules/RagAssistant.Modules.Documents.Tests` (normalization, metadata rules, derived status, validator, module boundary) and the integration tests in `tests/RagAssistant.Integration.Tests/Documents` (AC-1 to AC-19; AC-20 is the module boundary unit test). The AppHost was not run for this change, so the MigrationService's run against the dev database was not exercised; the integration fixture applies the same migration through `DatabaseMigrator`.
