# ADR 0006: PDF Import (Text Extraction Library and Original File Storage)

**Status:** Accepted (2026-10-14, together with [spec 010](../specs/010-pdf-import.md))
**Date:** 2026-10-14
**Relates to:** [roadmap](../roadmap.md) step 1.8, [ADR 0004](0004-document-versioning.md) (targets), [ADR 0005](0005-api-kernel-libraries.md) (dependency rule)

## Context

Content owners need to import PDFs up to 100 MB. `CLAUDE.md` requires an ADR for every new library or infrastructure component. Three choices are involved: how text and layout are read from a PDF, where the original binary lives, and how the corpus targets of ADR 0004 change.

## Decisions

### 1. Text extraction: PdfPig

`PdfPig` (NuGet id `PdfPig`, namespace `UglyToad.PdfPig`, Apache 2.0, managed code, no native dependencies) reads pages, words with font name, size and position, and the document outline. It runs in the Documents module only, behind an internal `IPdfTextExtractor`, so tests and a future replacement do not touch the pipeline. Its page segmentation (Docstrum) and reading-order detection handle multi-column pages (`UglyToad.PdfPig.DocumentLayoutAnalysis`; if these ship as a separate package, that package is added under this ADR). It extracts the text layer only: **no OCR**, no image content, no execution of PDF scripts or embedded files.

Whether a PDF needs OCR is decided at upload by an internal `IPdfProbe` over a bounded sample of pages (character counts, image coverage and glyph classes, no layout analysis). Scanned PDFs are rejected with `documents.requires_ocr`; PDFs with only some text-less pages are accepted and the pages are recorded on the version (`pages_without_text`), which is also the input a later OCR step would need. Full extraction repeats the check over all pages.

### 2. Original file storage: filesystem behind an interface

The original binary is stored outside PostgreSQL through an internal `IOriginalFileStore`. The first implementation writes to a local directory (`Documents:Storage:Path`) with content-addressed names (`pdf/<sha256>.pdf`), written to a temporary file and moved atomically. Nothing deletes blobs in this phase (documents are archived, never purged). An object-storage implementation can replace it without touching the module's features. The text formats keep their normalized content in the `content` column.

### 3. Targets (amends ADR 0004 decision 3)

| Target | Value |
|---|---|
| Corpus | ~100 documents / **~50k passages** (was ~10k: a 200-page PDF yields hundreds of passages) |
| Ingestion of a typical text document | < 1 min (unchanged) |
| Ingestion of a 200-page PDF | < 10 min, with visible progress |

Default pgvector HNSW settings remain sufficient at this size. Time to first token is unchanged.

## Consequences

- One new package (`PdfPig`), versioned in `Directory.Packages.props`.
- Layout analysis is best effort: columns are handled, but tables (no structure recovery), sidebars and unusual fonts can degrade reading order and heading detection; layout analysis costs extraction time and can be switched off by configuration. Evaluation (Phase 5) decides whether to invest further.
- Extraction runs in the API process; very large or hostile files are bounded by page, size and time limits, not isolated. Out-of-process extraction is a later option.
- The stored files must be included in any backup or deployment of the API's data, in addition to the database.

## Rejected

| Option | Reason |
|---|---|
| Original in PostgreSQL (`bytea` / large objects) | Bloats the database and WAL at 100 MB per file; the roadmap keeps binaries outside Postgres. |
| Object storage container (MinIO, Azure Blob) | A new infrastructure component and SDK for a demo; the interface keeps it possible later. |
| Commercial PDF libraries (iText, Aspose, Syncfusion) | Licensing; Syncfusion is excluded by ADR 0001. |
| OCR for scanned PDFs | Out of scope for the first release (`product.md`); they are detected and rejected at upload instead. |

## Implementation notes (2026-10-15)

- The NuGet package id is `PdfPig` (`UglyToad.PdfPig` on NuGet only lists an old alpha and a custom build); the namespace is still `UglyToad.PdfPig`. Version 0.1.16, Apache-2.0, same author and repository. Layout analysis ships in the same package.
- **Parser safety:** PdfPig cannot be cancelled and its lenient mode searches a whole file for structure, which took minutes on a 40 MiB file that merely started with `%PDF-`. The upload probe therefore checks the last 4 KiB for `startxref`, parses strictly, and both the probe and extraction are awaited with a bounded wait. A file the probe cannot read is accepted and ingestion reports the specific failure.
- **Browser viewer:** the download endpoint's `Content-Security-Policy: default-src 'none'; sandbox` does not stop Chromium's built-in PDF viewer: a headed run rendered the PDF and honoured `#page=N` (checked 2026-10-15).
- **HTTP timeouts:** the default 10 s and 30 s resilience timeouts failed slow embedding batches with an unexpected error; `ServiceDefaults` now allows 2 min and 5 min so the module's `EmbeddingTimeout` decides.
