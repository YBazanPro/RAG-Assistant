# ADR 0004: Document Versioning and Citation Snapshots

**Status:** Accepted
**Date:** 2026-10-13
**Relates to:** [roadmap](../roadmap.md) decisions D2, D3, D4; `product.md` challenge 5

## Context

Documents change. An answer that cited a passage must stay verifiable after the document is updated or archived. This shape is expensive to change once data exists, so it is fixed before the Documents data model (roadmap 1.1).

## Decisions

### 1. Versioned documents with stable passage identity

- `Document` has a stable identity and metadata (title, language, owner, tags, effective date, status).
- `DocumentVersion` belongs to a document; uploading a new file creates a new version and preserves history.
- `Passage` belongs to a version and has a stable ID, position, section path and parent section.
- Only the **active, indexed** version of a non-archived document is retrievable, unless history is explicitly requested.

### 2. Citation snapshots

Each assistant answer stores a snapshot of the passages it cited (passage ID, version, document title, section, excerpt text at answer time). Citations render from the snapshot, so they stay correct even if the live passage later changes or the document is archived. The UI marks citations whose source is superseded or archived.

### 3. Targets (D3)

Order-of-magnitude design targets, not guarantees; Phase 5 measures them:

| Target | Value |
|---|---|
| Corpus | ~100 documents / ~10k passages |
| Time to first token | < 3 s |
| Ingestion of a typical document | < 1 min |

At this scale the default HNSW settings are sufficient.

> **Amended 2026-10-14 by [ADR 0006](0006-pdf-import.md), decision 3:** the corpus target is ~100 documents / **~50k passages** (a 200-page PDF yields hundreds of passages), and a 200-page PDF is ingested in under 10 minutes with visible progress. The other targets are unchanged.

### 4. Retention (D4)

For the demo, conversations and request traces are **kept indefinitely**. Users can delete their own conversations. Traces never store full document text. Retention must be revisited before any real deployment.

## Consequences

- Storage grows with versions and snapshots; acceptable at demo scale.
- Chunking must produce stable passage IDs per version (IDs are never reused across versions).
- The `documents` and `chat` schemas stay separate: the snapshot is copied into the `chat` schema, so no cross-module foreign key exists.

## Rejected

| Option | Reason |
|---|---|
| Replace in place | Old citations break; no history. |
| Versions without snapshots | Answers could point at changed or archived text. |
