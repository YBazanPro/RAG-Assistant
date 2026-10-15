# Spec 010: PDF import

**Status:** Approved (2026-10-14)
**Date:** 2026-10-14
**Roadmap:** step 1.8 ([roadmap](../roadmap.md)) · **Builds on:** specs [006](006-documents-data-model-and-import.md), [007](007-background-ingestion-and-progress.md), [008](008-browse-corpus-and-documents-ui.md), [009](009-document-lifecycle.md) · **Decision records:** [ADR 0006](../adr/0006-pdf-import.md) (new), [ADR 0004](../adr/0004-document-versioning.md)

## Problem

Most organizational knowledge lives in PDFs: policies, manuals, reports. The corpus accepts only Markdown and plain text of at most 1 MiB, so a content owner has to convert files by hand, and citations cannot point at a page. This spec adds PDF import up to 100 MB: the original file is stored outside the database, the text layer is extracted page by page with headings inferred from the outline or the fonts, passages carry their page numbers, and users can open the original at the cited page. Scanned PDFs (no text layer) stay out of scope and fail visibly.

## Scope

### In

**API (Documents module)**

- Import (`POST /api/documents`) and new version (`POST …/versions`, spec 009) accept `.pdf` up to `Documents:Import:MaxPdfSizeBytes` (default 100 MiB). Text formats keep 1 MiB.
- A streaming multipart reader for both endpoints (no buffering of the whole file in memory), for all formats.
- `IOriginalFileStore` with a local-filesystem implementation; content-addressed, never deleted.
- PDF extraction in the ingestion pipeline: pages, reading order, outline or font-based headings, repeated header/footer removal, page numbers on passages.
- Failure codes for unusable PDFs; extraction limits (pages, time).
- Progress events inside long stages (amends spec 007).
- `GET /api/documents/{id}/versions/{number}/file` (original download/inline view).
- Migration: nullable `content`, new `storage_key`, check constraint, `ingestion_events.current/total`.
- Reprocess and new-version (spec 009) work for PDFs.
- ADR 0006 accepted (PdfPig, filesystem store, amended targets).

**BFF**

- A dedicated YARP route for the two upload endpoints with a 100 MiB + 64 KiB request body limit and a long activity timeout; other routes keep spec 005's limits.

**Web.Client**

- `DocumentUpload` accepts `.pdf`, sends the file through a JS upload module (progress bar, cancel).
- `IngestionStatus` shows a progress bar for `progress` events; `PassageCard` shows page numbers with an "Open page N" link; History/Metadata show format and a file link.

**Quality and docs**

- Tests (unit with generated PDFs, integration, bUnit/Web), a small committed sample PDF fixture, docs (roadmap, CLAUDE.md, `product.md` formats line, ADR 0004 target note).

### Out

- OCR / scanned PDFs, image content, drawings, form fields, annotations, attachments.
- DOCX (still "Later" in the roadmap); password-protected PDFs; PDF/A validation.
- Table structure recovery; guaranteed reading order on arbitrary layouts (columns are handled by layout analysis, other complex layouts are best effort, see Edge Cases).
- Out-of-process or sandboxed extraction; antivirus scanning.
- Object-storage implementation of `IOriginalFileStore`; blob deletion or garbage collection.
- Viewing the PDF inside the application (the browser's own viewer opens the file in a new tab).

## Domain Model

### Schema changes (one migration, `/migrate`)

| Table | Change |
|---|---|
| `document_versions` | `content` becomes **nullable**; new `storage_key text null` (≤ 300); check constraint `ck_document_versions_source`: PDF versions have `storage_key` and no `content`; text versions have `content` and no `storage_key` |
| `ingestion_events` | new `current int null`, `total int null` (progress counters) |

`EventStatus` gains `Progress` (stored as a string, so no column change). `SourceFormat.Pdf` already exists (spec 006). Existing rows are unaffected.

### PDF version fields

| Field | Value for a PDF version |
|---|---|
| `SourceFormat` | `Pdf` |
| `Content` | `null` |
| `StorageKey` | `pdf/<sha256>.pdf` (relative to `Documents:Storage:Path`) |
| `ContentHash` / `Document.LatestContentHash` | SHA-256 (hex) of the **file bytes**, so duplicates are detected at upload without parsing. The same text as Markdown and as PDF are different content |
| `SizeBytes` | File length in bytes |
| `ContentType` | As declared by the client; informational |
| `OriginalFileName` | Final path segment, as for text |

The extracted text is **not** stored on the version; passages carry it, and a reprocess (spec 009) re-extracts from the stored file. A reprocess version copies `StorageKey` and `ContentHash` (the blob is shared).

### Original file store

- Layout: `<Documents:Storage:Path>/pdf/<sha256>.pdf`, `<path>/tmp/<guid>` for in-flight uploads. The **AppHost** chooses the location: it resolves `<repo root>/.data/documents` (git-ignored), creates it, and passes it to the API as the environment variable `Documents__Storage__Path`, so the path is visible in the Aspire dashboard. When the API runs without the AppHost, the default is `./.data/documents` under its content root. Tests use a temporary directory per factory.
- Upload writes to `tmp/`, hashing while streaming; on success it is moved to its final name (atomic rename). If the blob already exists (re-import, rollback, archived duplicate), the temp file is discarded. The database transaction is committed **after** the blob is in place; a failure after the move leaves an unreferenced blob, which is harmless because content-addressed.
- Startup validates the directory (creates it, writes and deletes a probe file); failure stops the API with an options validation error. A sweep at startup deletes `tmp/` files older than one hour.
- Nothing deletes final blobs in this phase.

### PDF extraction and chunking

Extraction produces the same intermediate form as the Markdown parser: an ordered list of **source blocks** (text, section path, page). The packing, overlap and minimum-size rules of spec 007 then apply unchanged; passage `Text` is the extracted plain text, `PageStart`/`PageEnd` are the first and last page of any text in the passage (including overlap text), and the embedding input is built as in spec 007.

1. **Pages.** Pages are read in order. A page "has text" when it has at least `Documents:Pdf:MinCharsPerTextPage` (20) non-whitespace characters.
2. **Layout analysis and reading order.** Each page is segmented into text blocks with PdfPig's page segmentation (Docstrum), and the blocks are ordered with its reading-order detection, so multi-column pages read column by column. Blocks are split into lines from word positions. A hyphen at a line end followed by a lowercase letter joins the word. Lines of one paragraph are joined with a space. `Documents:Pdf:LayoutAnalysis=false` falls back to plain line grouping in content order (an escape hatch for pages where analysis is too slow or garbles the order). Table cells are segmented like any text: no table structure is recovered.
3. **Paragraphs.** A new block starts at a vertical gap larger than 1.5 times the page's median line spacing, or at a heading.
4. **Margins.** Lines that appear in the top or bottom margin band (10% of page height) on at least half of the pages with the same normalized text (digits ignored), and lines that consist only of a page number, are removed. The count is reported in the Extracting event. Disabled by `Documents:Pdf:RemoveRepeatedMargins=false`.
5. **Headings, outline first.** When the PDF has an outline (bookmarks), its tree defines the section chain: each entry targets a page; the section starts at the line on that page whose normalized text equals the entry title, else at the top of the page. Outline levels beyond the third are folded into the third. Text before the first outline entry has an empty path.
6. **Headings, font fallback.** Without an outline (or with one that targets no valid page), the body font size is the most frequent size by character count. A block is a heading when it is a single line of at most `MaxHeadingChars` (120) characters and its size is at least `HeadingMinSizeRatio` (1.15) times the body size, or it is bold at body size or larger and shorter than 80 characters and ends without a period. Distinct heading sizes are ranked into at most `MaxHeadingLevels` (3) levels, largest first.
7. **No tables or images.** Table cells come out as text lines in reading order; images and drawings are ignored.
8. **Result.** Zero passages is a failure (`documents.no_passages`, spec 007).

**Extraction progress.** The Extracting stage writes `Progress` events with `current`/`total` pages (about every 10%, at most 10 per run). The Extracting `Completed` message reports counts: `200 pages, 3 without text, 41 margin lines removed`.

### Progress events (amends spec 007)

- `IngestionEvent` gets `Current`/`Total` and `EventStatus.Progress`. The Embedding stage writes `Progress` events after a batch whenever `floor(done × 10 / total)` increases and the stage is not finished (at most 9 per stage), with `current` = passages embedded and `total` = passages. The Extracting stage does the same for PDF pages.
- The SSE payload (spec 007) gains `current` and `total` (nullable); `status` can be `progress`. Terminal detection and `Last-Event-ID` behaviour are unchanged.
- **Amendment to spec 007 AC-2:** `Progress` events may appear between a stage's `Started` and `Completed` events; the order and presence of `Started` / `Completed` are unchanged, and small documents (few batches) may have none.

## API Contract

### Changed endpoints

| Verb | Route | Change |
|---|---|---|
| POST | `/api/documents` | `file` may be `.pdf` (case-insensitive); size limit by format; streamed |
| POST | `/api/documents/{id}/versions` | same |
| GET | `/api/documents/{id}/versions/{number:int}/file` | **new**, below |

Both POST endpoints read the multipart body with a streaming reader. The `file` part is streamed to the store's `tmp/` area (hashing as it goes); other parts are read as small fields; part order does not matter. The size limit is chosen from the file's extension when its part starts: 1 MiB for text, `MaxPdfSizeBytes` for PDF; exceeding it aborts the read with `400 documents.file_too_large` (a whole request over the Kestrel limit of `MaxPdfSizeBytes` + 64 KiB is `413`, as today). The limit for non-file fields stays 64 KiB in total.

New validation codes (top-level `code`, message under `errors.file`):

| Code | When |
|---|---|
| `documents.invalid_pdf` | Extension `.pdf` but the first 1024 bytes do not contain `%PDF-` |

Existing codes, the duplicate `409` (hash of bytes), metadata validation and `202` bodies are unchanged. `ImportDocument.Response` / `NewVersion.Response` return `status: "queued"`; `GET` responses show `sourceFormat: "pdf"`.

### Download

| Verb | Route | Success | Errors |
|---|---|---|---|
| GET | `/api/documents/{documentId:guid}/versions/{number:int}/file` | `200`, the original bytes, `Accept-Ranges: bytes` (range requests supported) | 401 · 404 `documents.not_found` / `documents.version_not_found` / `documents.file_missing` |

- PDF: `Content-Type: application/pdf`, `Content-Disposition: inline; filename*=UTF-8''<name>`.
- Text versions: the normalized `Content` as `text/plain; charset=utf-8`, `Content-Disposition: attachment`.
- Always `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`, and `Cache-Control: private, no-store`.
- `documents.file_missing` (the blob is absent from the store) is a `404` with a logged error.

`PassageItem` (spec 008) gains `pageStart` and `pageEnd` (nullable); `VersionItem` and `GetDocument` expose `hasOriginalFile`-free data already (`sourceFormat`), so the UI builds the file link from the route.

## Authorization

| Endpoint | Policy | Knowledge Seeker | Content Owner | Maintainer | Anonymous |
|---|---|---|---|---|---|
| `POST` import / new version with a PDF | `ContentOwner` | 403 | 202 | 202 | 401 |
| `GET …/versions/{n}/file` | `KnowledgeSeeker` | 200 | 200 | 200 | 401 |

Seekers may open the original of any version they can browse (spec 008's decision to let seekers read all versions applies).

## UI Design

| Element | Change |
|---|---|
| `DocumentUpload` | Accepts `.md`, `.markdown`, `.txt`, `.pdf`. Client checks: extension; size ≤ 1 MiB for text, ≤ 100 MiB for PDF (constants mirroring the options). The upload is sent by a small JS module through an `IFileUploader` abstraction: `multipart/form-data` with `X-Requested-With: ragassistant`, same-origin session cookie, a **progress bar** (bytes sent / total), **Cancel** (aborts the request), and the same status/ProblemDetails mapping as before. The 5 dialog states of spec 008 gain "uploading with progress". |
| `IngestionStatus` | Determinate progress bar when the latest event is `progress` (`current/total`), label such as "Embedding 120 of 540 passages"; the stage stepper is unchanged. |
| `PassageCard` | PDF passages show "p. 12" or "pp. 12–13"; a link "Open page 12" opens `/api/documents/{id}/versions/{n}/file#page=12` in a new tab (`rel="noopener"`); text passages show none. |
| `VersionHistory` / `DocumentMetadataPanel` | Format "PDF", size in MB, and a "Download original" / "Open original" link per version. |
| `DocumentUpload` (new-version mode, spec 009) | Same PDF support and progress. |

The JS module is a small ES module (`wwwroot/js/upload.js`), loaded lazily after the first render (never during prerender), using `XMLHttpRequest` for progress and abort. Uploads do not pass through the .NET `HttpClient`, so the file is not buffered in the WebAssembly heap.

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| `.pdf` file that is not a PDF (no `%PDF-`) | `400 documents.invalid_pdf`, nothing stored |
| PDF over 100 MiB | `400 documents.file_too_large` while streaming (upload stops); over the Kestrel limit `413` |
| Text file between 1 MiB and 100 MiB | `400 documents.file_too_large` (the 1 MiB text limit applies by extension) |
| Same PDF bytes as the active document's latest version | `409 documents.duplicate_content` + `existingDocumentId` (hash of bytes; temp file discarded) |
| Same bytes as an archived document | Accepted; the blob is reused |
| Two identical PDFs uploaded at once | One `202`, one `409` (partial unique index, spec 006); the blob is written once |
| Client cancels or disconnects mid-upload | Temp file removed, nothing persisted |
| Disk full / store not writable | `500` ProblemDetails (global handler), temp file removed, nothing persisted |
| Crash between blob move and commit | Unreferenced blob; harmless |
| Encrypted (password-protected) PDF | Ingestion `Failed`, `documents.pdf_encrypted` |
| Corrupt or unparsable PDF | `Failed`, `documents.pdf_invalid` (content-free message) |
| More than `MaxPages` (2000) pages | `Failed`, `documents.pdf_too_many_pages` |
| Extraction exceeds `ExtractionTimeout` (15 min) | `Failed`, `documents.extraction_timeout` |
| Less than 10% of pages have text | `Failed`, `documents.no_text_layer` ("looks like a scanned PDF; OCR is not supported") |
| 10% or more pages have text, some empty | Proceeds; the Extracting `Completed` message states `N pages without text` |
| Blob missing at ingestion or download | Ingestion `Failed` with `documents.file_missing`; download `404 documents.file_missing`; the error is logged |
| Retry/reprocess of a failed PDF | Works as in specs 007 and 009 (deterministic failures fail again) |
| New version changes format (PDF ↔ Markdown ↔ text) | Allowed |
| Multi-column pages | Read column by column through layout analysis |
| Tables, rotated text, sidebars, unusual layouts | Best-effort reading order; table cells come out as text blocks; accepted limitation (ADR 0006) |
| Layout analysis too slow on a dense page | Bounded by the extraction timeout; the owner can set `LayoutAnalysis=false` and retry |
| PDF containing scripts, embedded files, forms | Ignored, never executed; only the text layer is read |
| Ligatures or odd encodings producing replacement characters | Kept as extracted; no repair |
| Very long single line (no spaces) | Split by the chunker's hard-limit rule (spec 007) |
| API restart mid-extraction | Run re-queued from the start (spec 007 recovery); no partial passages |
| Download of a version whose document is archived | Allowed (read access) |
| Range request on the download | Supported (206) so browsers can seek large PDFs |

## Non-Functional Requirements

- **Upload:** a 100 MiB upload is received with constant memory (buffer size, not file size). The request body limit is raised for the two upload routes only; BFF and API timeouts for these routes allow slow connections (activity timeout 15 minutes).
- **Extraction time:** a 200-page text PDF is indexed in under 10 minutes on a dev machine with warm Ollama (ADR 0006 target). Not enforced by a test; measured by hand and by Phase 5.
- **Extraction memory:** pages are processed one at a time and released; the whole file is never loaded as a byte array. Extraction runs off the request path on the worker.
- **Configuration** (`ValidateOnStart`, all > 0 where numeric):

| Key | Default |
|---|---|
| `Documents:Import:MaxPdfSizeBytes` | 104 857 600 |
| `Documents:Storage:Path` | `./.data/documents` (writable) |
| `Documents:Pdf:MaxPages` | 2000 |
| `Documents:Pdf:ExtractionTimeout` | 15 min |
| `Documents:Pdf:MinCharsPerTextPage` | 20 |
| `Documents:Pdf:MinTextPageRatio` | 0.10 |
| `Documents:Pdf:RemoveRepeatedMargins` | true |
| `Documents:Pdf:LayoutAnalysis` | true |
| `Documents:Pdf:HeadingMinSizeRatio` / `MaxHeadingLevels` / `MaxHeadingChars` | 1.15 / 3 / 120 |

- **Telemetry:** counters `documents.imported` (tag `format` now includes `pdf`), `documents.pdf.pages.extracted`, histogram `documents.pdf.extraction.duration` (seconds), existing ingestion metrics; `ingestion.run` stage spans cover Extracting for PDFs. Logs carry version id, page counts, outcome; never text.
- **Security:** filenames never become paths (store keys come from the hash); the download sets `nosniff`, a sandboxing CSP and `no-store`; the PDF is parsed, never rendered, by the server; limits bound pages, size and time.
- **Accessibility:** the progress bar has `role="progressbar"` with `aria-valuenow/min/max` and a text label; Cancel is keyboard-operable; errors are announced.
- **Architecture:** extraction and the store are internal to the Documents module (`IPdfTextExtractor`, `IOriginalFileStore`); the only new package is `UglyToad.PdfPig` (ADR 0006); no reference to another module; `arch-check` passes.

## Integrations

- **PdfPig** (`UglyToad.PdfPig`): the single new package.
- **Filesystem:** a data directory outside the database, chosen by the AppHost (see Domain Model) and passed to the API through configuration. Backups must include it. The API runs as a process, so it is a plain folder on the developer's machine, not a container volume.
- **BFF (spec 005):** a dedicated YARP route for `POST /api/documents` and `POST /api/documents/{id}/versions` with the larger body limit and timeout; everything else is unchanged. The GET file route passes through the existing catch-all route, with response streaming (no buffering) and range support preserved.
- **Spec 007 worker:** the Extracting stage dispatches by `SourceFormat`; the Chunking, Embedding and Storing stages are unchanged except for progress events.
- **Spec 009:** new version and reprocess accept PDFs; reprocess copies the blob reference.
- **Other modules:** none.

## Acceptance Criteria

Upload and storage (integration tests; PDFs are generated in tests or taken from a committed fixture)

- **AC-1** Given an owner and a valid text-layer PDF with title and owner name, when `POST /api/documents`, then `202`, a document with a version `SourceFormat = Pdf`, `Content = null`, `StorageKey = pdf/<sha256>.pdf`, `ContentHash` equal to the SHA-256 of the file bytes, `SizeBytes` equal to the length, a queued run, and the file exists in the store with identical bytes.
- **AC-2** Given a PDF slightly over 1 MiB, then `202`; given a text file over 1 MiB, then `400 documents.file_too_large`; given a PDF of `MaxPdfSizeBytes + 1`, then `400 documents.file_too_large` and nothing stored (temp file removed).
- **AC-3** Given a `.pdf` file whose content is not a PDF, then `400 documents.invalid_pdf` and nothing persisted.
- **AC-4** Given the same PDF bytes imported twice, then the second returns `409 documents.duplicate_content` with `existingDocumentId` and the store holds one blob; given two simultaneous identical imports, then one `202` and one `409`; given the same bytes as an archived document, then `202` and the blob is reused.
- **AC-5** Given the file part sent before or after the metadata fields, then the result is the same; given a client disconnect mid-upload, then no temp file remains and nothing is persisted.
- **AC-6** Given a large PDF (generated, tens of MiB), when imported, then peak memory growth of the request stays well below the file size (asserted with a coarse bound on `GC.GetTotalAllocatedBytes`/working set), proving streaming.
- **AC-7** Given the store directory is not writable, when the API starts, then startup fails with an options validation error; given stale `tmp/` files older than one hour at startup, then they are deleted.
- **AC-8** Given the migration applied to a database with existing text versions, then `content` is nullable, `storage_key` and the events' `current`/`total` columns exist, the check constraint rejects a PDF version with `content` and a text version without it, and existing rows are unchanged.

Extraction and chunking (unit tests on generated PDFs and a fixture)

- **AC-9** Given a two-page PDF with known text, then ingestion produces passages whose text equals the page text (reading order), with `PageStart/PageEnd` equal to the page(s) of their text; a passage spanning pages 1 and 2 has `PageStart = 1`, `PageEnd = 2`.
- **AC-10** Given a PDF with an outline of two levels, then `SectionPath` follows the outline (entry matched to its line or the top of its page); levels beyond three are folded into the third; text before the first entry has an empty path.
- **AC-11** Given a PDF without an outline and with a larger bold title and smaller section headings, then those lines become `SectionPath` entries (up to three levels) and are not included in passage `Text`; body text with uniform font produces no headings.
- **AC-12** Given a header and a footer repeated on every page and a page number line, then they are absent from passage text, and the Extracting `Completed` message reports the removed count; with `RemoveRepeatedMargins=false` they are kept.
- **AC-13a** Given a generated two-column page, then the text of the whole left column precedes the right column in the passages; given `LayoutAnalysis=false`, then plain content order is used.
- **AC-13** Given a hyphenated line break (`inter-` / `national`), then the word is joined; given two paragraphs separated by a larger vertical gap, then they are separate blocks.
- **AC-14** Given a PDF whose pages are 100% without text, or with 9% of pages with text, then the run fails `documents.no_text_layer`; given 10% or more, then it proceeds and the message states how many pages had no text.
- **AC-15** Given an encrypted PDF, a corrupt PDF, a PDF with 2001 pages and an extraction that exceeds the timeout (controlled `TimeProvider`), then the run fails with `documents.pdf_encrypted`, `documents.pdf_invalid`, `documents.pdf_too_many_pages` and `documents.extraction_timeout`, with content-free messages and no passages.
- **AC-16** Given a missing blob when ingestion starts, then the run fails `documents.file_missing`.
- **AC-17** Given the chunker settings of spec 007, then PDF passages respect `MaxTokens`, overlap and minimum size exactly as text passages do (same packing code), and every passage has an embedding built from the section path plus text.

End to end (integration, dedicated ingestion factory)

- **AC-18** Given an imported PDF and a working fake generator, then the version becomes `Indexed` with `PassageCount`, the document's `ActiveVersionId` is set, and passages have `PageStart/PageEnd`.
- **AC-19** Given a new version (spec 009) in a different format (PDF over Markdown and the reverse), then it is accepted and indexed and supersedes the previous one; given a reprocess of a PDF, then the new version reuses `StorageKey` and `ContentHash` and re-extracts.
- **AC-20** Given `Duplicate` and `file_missing` paths, then the corresponding metrics (`documents.imported` tag `format=pdf`, `documents.import.rejected`, `documents.pdf.pages.extracted`, `documents.pdf.extraction.duration`) record the expected values.

Progress events

- **AC-21** Given a PDF of 50 pages and 300 passages (16 per batch), then the log contains `Progress` events with `current/total` for Extracting (pages) and Embedding (passages), at most 9 per stage, strictly increasing, none at 100%, between the stage's `Started` and `Completed`; given a small document (3 batches), then at most 2.
- **AC-22** Given the SSE stream, then `progress` events carry `current` and `total`; `Last-Event-ID` resume and terminal detection behave as in spec 007; spec 007's AC-2 holds for the order of `Started`/`Completed`.

Download

- **AC-23** Given a PDF version, when a seeker calls `GET …/versions/{n}/file`, then `200`, the exact bytes, `application/pdf`, inline disposition, `nosniff`, the sandbox CSP and `no-store`; a `Range: bytes=0-99` request returns `206` with those bytes.
- **AC-24** Given a text version, then the normalized content is returned as `text/plain; charset=utf-8` with `attachment`; given an unknown document, version or a missing blob, then `404` with `documents.not_found`, `documents.version_not_found`, `documents.file_missing`; given an anonymous caller, then `401`.
- **AC-25** Given `GET …/passages`, then items include `pageStart`/`pageEnd` (null for text).

Authorization

- **AC-26** Given a KnowledgeSeeker, `POST` with a PDF returns `403` (the request is rejected before the body is read); given anonymous, `401`; given an owner and a maintainer, `202`.

BFF

- **AC-27** Given the real BFF against `StubApi`, then a multi-megabyte upload to `POST /api/documents` reaches the API intact, incrementally (the first bytes arrive at the API before the client finishes) and above the default 28.6 MB limit up to 100 MiB; other routes keep the default limit; the file `GET` is streamed without buffering and range requests pass through.

UI (bUnit / Web tests)

- **AC-28** Given the upload dialog, then `.pdf` is accepted; a PDF over 100 MiB or a text file over 1 MiB shows a client-side error without sending; the `IFileUploader` is called with the right URL, headers (`X-Requested-With`) and fields.
- **AC-29** Given progress callbacks from the uploader, then a progressbar with `aria-valuenow` reflects them; Cancel aborts the upload and returns the dialog to idle with values kept; the 202 and error mappings of specs 008 and 009 still apply (409 duplicate link, 400 `invalid_pdf` under the file picker, 413, network).
- **AC-30** Given `progress` events on the stream, then `IngestionStatus` shows a determinate bar and label ("Embedding 120 of 540 passages"); events with no counters keep the stepper only.
- **AC-31** Given a PDF passage with pages, then `PassageCard` shows "p. 12" / "pp. 12–13" and an "Open page 12" link to `…/file#page=12` that opens in a new tab with `rel="noopener"`; text passages show neither.
- **AC-32** Given the History tab and metadata panel, then PDF versions show format, size and a link to the original; failures show the new codes' messages.
- **AC-33** Given prerendering, then the JS upload module is not loaded or called until the dialog is used (no JS interop during prerender).

Architecture and docs

- **AC-34** The Documents project references `UglyToad.PdfPig` and `RagAssistant.Shared` only; no other module; `Directory.Packages.props` holds the version; ADR 0006 is `Accepted`; `arch-check` passes.
- **AC-35** The roadmap marks 1.8 done and Phase 1 complete; `product.md`'s formats line lists PDF; ADR 0004's corpus target note points to ADR 0006; CLAUDE.md mentions PDF and the store.

## Testing Approach

- **Unit tests** for the extractor with PDFs generated in the test project (PdfPig's `PdfDocumentBuilder` can write PDFs with chosen fonts, sizes, outline, headers) plus one small committed fixture PDF (a real-world sample, a few pages) for a smoke test; the chunker tests reuse spec 007's.
- **Integration tests** (Testcontainers, temporary store directory per factory, dedicated ingestion factory with the controllable fake generator) for the upload, duplicate, store, download and end-to-end criteria. Time limits use the controlled `TimeProvider`.
- **Large-upload test** (AC-6, AC-27) uses a generated large PDF (valid header, bulk padding stream) so no large fixture is committed.
- **Web tests:** `IFileUploader` is faked in bUnit; the JS module itself is verified by hand in the browser (and recorded in Implementation Notes) because there is no browser test harness (Playwright is out of scope).
- **Manual:** run the AppHost, import a real multi-page PDF with an outline, watch extraction and embedding progress, browse passages with page numbers, open page N, and import a scanned PDF to see `documents.no_text_layer`.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| OCR for scanned PDFs | Fail with `documents.no_text_layer` | Content owners need scans |
| Table structure recovery, perfect reading order on arbitrary layouts | Column-aware layout analysis, tables as text blocks | Evaluation (Phase 5) shows retrieval misses on such files |
| Out-of-process / sandboxed extraction | In the API, bounded by limits | Hostile or very large files appear |
| Object-storage `IOriginalFileStore`, blob garbage collection | Filesystem, never deleted | Deployment beyond one machine, or storage pressure |
| Password-protected PDFs | Fail with `documents.pdf_encrypted` | Users need to supply a password |
| In-app PDF viewer with highlighted passage | Open the file in a new tab at the page | Citation UX work (Phase 6) |
| DOCX import | Not supported | Roadmap "Later" trigger |
| Size limits exposed by the API | UI constants mirror the defaults | Limits become configuration-heavy |
| Antivirus scanning of uploads | None | Any non-demo deployment |

## Open Questions

None. Resolved in review (2026-10-14): corpus target raised to ~50k passages; the AppHost provides the storage folder (`Documents__Storage__Path`); progress counters are nullable `current`/`total` columns; column-aware layout analysis is included now (tables stay unstructured).
