# Spec 020: First-release polish

**Status:** Implemented (2026-10-20); AC-27 and AC-28 pending a manual check
**Date:** 2026-10-18
**Roadmap:** Phase 5.3 and Phase 6 · **Builds on:** specs [005](005-web-shell.md), [009](009-document-lifecycle.md), [012](012-chat-conversations-and-grounded-answers.md), [013](013-chat-ui.md), [014](014-follow-ups-feedback-and-deletion.md), [016](016-private-and-shared-documents.md), [017](017-diagnose-traces-admin-and-metrics.md), [018](018-evaluation-and-french-english.md) · **Decision records:** [ADR 0004](../adr/0004-document-versioning.md) (citations of superseded or archived sources)

**Prerequisite:** spec 018 (the harness and its report format; questions in English or French). Spec 019 is not required.

## Problem

- **The home page is a title and one button** (`Home.razor`). It doesn't orient a new user, suggest what to ask, or show what the corpus contains (product.md, "Home").
- **Old citations look current.** ADR 0004 requires the UI to mark citations whose source is superseded or archived, and nothing does yet.
- **Evaluation reports live only on the disk of whoever ran the harness** (spec 018). Maintainers can't see or compare runs in the app.
- **Conversations can't be found, renamed or taken elsewhere.**
- **The first release needs an accessibility pass and a README** that lets someone clone, run and demo the app.

## Scope

### In

**Home page (`/`)**

- The value statement (plain words, no claims about accuracy), the primary *Ask a question* action visible without scrolling, and **suggested questions**.
- An **active corpus summary**: count of active documents the user can read, per language; the last ingestion date.
- **Recent documents:** 5 most recently updated active documents the user can read.
- A link to the README.
- Same page for every role; actions the user lacks are hidden as today.

**Suggested questions (Documents module)**

- **Generated at ingestion.** After a version is indexed, a non-blocking step asks the chat model (`IChatClient`, structured output) for 3 questions that the version's passages answer, written in the document's language.
- **Checked by retrieval.** Each generated question is kept only if retrieval, run as the document's owner, returns that document with `Confident` evidence.
- **Stored on the version** as `SuggestedQuestions` (0–3 strings). The active version's questions are the document's.
- **The owner can edit or clear them** on the document detail page (ContentOwner, `If-Match` like metadata). Edited questions are kept on reprocess, and a new version generates fresh ones.
- **Shown** on the home page and in the empty state of a new conversation: up to 6, drawn at random from active documents the user can read (`ReadableBy`). Clicking one asks it in a new conversation (home) or the current one (empty state).
- **Failure is never fatal:** if generation or the check fails, the version stays indexed, and the failure is recorded on the version (`suggestions: failed`, with a code). In demo mode (`Chat:Provider = Demo`) nothing is generated (`suggestions: skipped`).

**Version UX on citations**

- **Each citation gets a source state**, computed when the conversation is read:
  - `current`: the cited version is the active version;
  - `newer-unchanged`: a newer active version contains a passage with the same normalised text;
  - `newer-changed`: a newer active version exists and no passage matches;
  - `archived`: the document is archived;
  - `unavailable`: the user can no longer read the document (spec 016).
  Passage IDs are never reused across versions (ADR 0004), so the comparison is on normalised text (NFC, collapsed whitespace, SHA-256) of the snapshot against the active version's passages.
- **Badges** in `CitationItem`: *Newer version*, *Changed in newer version*, *Archived*. No badge for `current`.
- **`SourcePreview`** shows "This source has changed since the answer" for `newer-changed`, "A newer version contains this passage unchanged" for `newer-unchanged`, and offers *Open current version* (the document detail page at the active version) for both. The snapshot text is still what is shown.

**Evaluation results in admin (new `Evaluation` module, roadmap 5.3)**

- **A new module** added with `/scaffold`. It has its own `evaluation` schema and DbContext, is registered in the MigrationService, and has no contract with other modules.
- **Upload:** the harness `run` and `judge` commands upload their report when they finish (`--no-upload` opts out). `upload <report>` covers older files. Upload is idempotent by run id, and a judged report replaces the run's judged metrics.
- **Admin tab *Evaluation*** on `/admin`:
  - a list of runs (date, chat model, retrieval mode, rerank, dataset and corpus hashes, headline metrics);
  - a run detail page (metrics per category and per language, configuration, broken cases, per-case results);
  - a comparison of two runs (metric deltas, configuration differences highlighted, like the harness `compare`).
- **Delete:** a maintainer can hard delete a run, with confirmation.

**Chat conveniences**

- **Rename a conversation** (owner): title 1–200 characters, trimmed; an empty title reverts to the generated one.
- **Search conversations** (owner's own): the conversation list filters by title and question text, case- and accent-insensitive substring, server-side, paged.
- **Export as Markdown** (owner): `GET /api/chat/conversations/{id}/export`. It contains the title, the date, each question and answer in order (answer status noted for refused, partial and failed), each answer's citations as a numbered source list (title, section, version, pages, source state), and no passage text beyond the snapshot excerpt. It downloads through the BFF with a plain link.
- **Copy answer with sources:** copies one answer's text with its `[n]` markers and a numbered source list (same format as the export) to the clipboard. A blocked clipboard shows the text to select, like *Copy reference*.

**Accessibility pass (WCAG 2.2 AA target)**

- **Keyboard paths** through Journeys 1–3 (product.md): import a document, ask and open a citation, browse a document. Visible focus, logical order, focus returned after dialogs and panels, no traps.
- **Screen-reader labels** for citations (marker, title, state), answer status, streaming progress (polite live region), badges and icon-only buttons.
- **Contrast** of badges and states in light and dark themes.
- **Verified** by Playwright smoke tests for the keyboard paths and focus order, plus role and name checks through Playwright's accessibility snapshot (no new library). The manual checklist (contrast, NVDA on citations) is recorded in this spec's *Verification* section.

**README**

- Clone-and-run: prerequisites (.NET 10 SDK, Docker, GPU note from spec 018), `dotnet run --project src/RagAssistant.AppHost`, launch profiles (`https`, `https-demo`, `https-rerank` from spec 019).
- Test users and roles (from the realm file), the sample corpus `seed` command, a short demo script (French and English), how to run tests, smoke tests and the harness.

### Out

- UI localization (the app stays English; suggested questions and answers are content, in their own language).
- Conversation sharing, pinning, folders, bulk export, PDF export.
- Editing suggested questions by anyone but the owner; suggestions for private documents shown to anyone but their owner.
- Automated axe-core checks (new dependency).
- Evaluation runs started from the admin area (the harness stays a console tool).
- The Knowledge module, DOCX import, OCR (roadmap "Later").

## Domain Model

| Module | Entity | Change |
|---|---|---|
| Documents | `DocumentVersion` | `SuggestedQuestions` (`text[]`, 0–3, each ≤ 200 chars), `SuggestionsStatus` (`pending` \| `ready` \| `failed` \| `skipped` \| `edited`), `SuggestionsError` (code, nullable). |
| Documents | `Passage` | `TextHash` (SHA-256 of normalised text), backfilled by the migration; used for the citation source state. |
| Chat | `Conversation` | `TitleEdited` (bool): a renamed title is kept; an empty rename reverts to the generated title. |
| Evaluation | `EvaluationRun` (new) | `Id` (run id from the report), `StartedAt`, `ChatModel`, `EmbeddingModel`, `JudgeModel?`, `RetrievalMode`, `Rerank`, `DatasetHash`, `CorpusHash`, `GitCommit?`, `Configuration` (`jsonb`), `Metrics` (`jsonb`), `Cases` (`jsonb`), `UploadedBy` (`sub`), `UploadedAt`. |

The suggestion step is not an ingestion stage that can fail a run. It runs after `indexed` and is recorded on the version. Progress events (spec 007) add a `suggestions` event so the detail page can update.

New Documents contract for Chat: `IDocumentSources.GetStatesAsync(IReadOnlyCollection<CitedPassage>, viewerSub, ct)` → a source state per cited passage, where `CitedPassage` is (document id, version id, text hash). Chat stores `TextHash` on `CitationSnapshot` (backfilled from the snapshot text).

The home page reads Documents endpoints directly (no new contract).

## API Contract

| Verb | Route | Request | Response | Errors | Policy |
|---|---|---|---|---|---|
| GET | `/api/documents/suggestions` | `count` (1–12, default 6) | `200 [{ question, documentId, documentTitle, language }]` | 400 | KnowledgeSeeker |
| GET | `/api/documents/summary` | — | `200 { activeDocuments, byLanguage: [{language, count}], lastIngestedAt, recent: [{id, title, updatedAt, language}] }` | — | KnowledgeSeeker |
| PUT | `/api/documents/{id}/suggestions` | `If-Match`; `{ questions: string[] }` (0–3) | `200` + `ETag` | 400 · 404 · 412 · 428 | ContentOwner (owner) |
| GET | `/api/documents/{id}` | — | adds `suggestedQuestions`, `suggestionsStatus` | — | KnowledgeSeeker |
| PATCH | `/api/chat/conversations/{id}` | `{ title }` | `200` conversation summary | 400 · 404 | KnowledgeSeeker (owner) |
| GET | `/api/chat/conversations` | adds `q` (≤ 200 chars) | paged list as today, filtered | 400 | KnowledgeSeeker |
| GET | `/api/chat/conversations/{id}` | — | each citation adds `sourceState` | — | KnowledgeSeeker (owner) |
| GET | `/api/chat/conversations/{id}/export` | — | `200 text/markdown; charset=utf-8`, `Content-Disposition: attachment; filename="<slug>-<date>.md"` | 404 | KnowledgeSeeker (owner) |
| POST | `/api/admin/evaluation/runs` | report JSON (≤ 10 MiB) | `201` new · `200` replaced | 400 · 413 | Maintainer |
| GET | `/api/admin/evaluation/runs` | `page`, `pageSize`, `chatModel` | paged list | 400 | Maintainer |
| GET | `/api/admin/evaluation/runs/{id}` | — | full run | 404 | Maintainer |
| GET | `/api/admin/evaluation/compare` | `a`, `b` | metric deltas + configuration differences | 400 · 404 | Maintainer |
| DELETE | `/api/admin/evaluation/runs/{id}` | — | `204` | 404 | Maintainer |

Mutating calls go through the BFF's `X-Requested-With` CSRF rule as today. The harness calls the API directly with a maintainer token (spec 018).

## Authorization

- Suggestions and summary only count and return documents the caller can read (`ReadableBy`, spec 016). Private documents' suggestions are shown to their owner only.
- Only a document's owner (`CreatedBy`) edits its suggestions, like metadata (spec 016).
- Rename, search, export and copy act on the caller's own conversations; another user's conversation is a 404.
- Source states follow `ReadableBy`: a document the user can no longer read is `unavailable`, with no title beyond the snapshot.
- Evaluation endpoints: Maintainer only. Reports contain question texts and model answers from the sample corpus only. They contain no user data, and the upload rejects a report whose scope isn't the `sample` tag.

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| Chat model unavailable during suggestion generation | `suggestions: failed (model_unavailable)`; version stays indexed; reprocess retries. |
| Generated question fails the retrieval check | Dropped; fewer than 3 kept is fine; 0 kept → `ready` with an empty list. |
| Owner edits suggestions, then reprocesses | Edited questions kept (`edited`); a new version regenerates. |
| Concurrent edits of suggestions | `If-Match` → `412`. |
| No readable documents | The home page shows the empty-corpus state (EmptyState) with *Import a document* for content owners. |
| Fewer than 6 suggestions available | Show what exists; none → no suggestions block. |
| Cited version superseded twice | The state is computed against the current active version only. |
| Document has no active indexed version (re-ingesting) | `newer-changed` is not claimed; the state is `current` if the cited version is still the active one, else `newer-changed` only once the new version is indexed. |
| Snapshots from before this spec (no hash) | Hash computed from the stored excerpt by the migration. |
| Rename to whitespace | Reverts to the generated title. |
| Rename to > 200 characters | `400 validation.failed`. |
| Search with `q` matching nothing | Empty list with "No conversations match". |
| Export of a conversation with a streaming answer | That answer is exported with status `in progress` and whatever text is stored. |
| Export of a conversation with unavailable sources | Sources listed as "Source no longer available", no title. |
| Clipboard blocked | Fallback text field to select, as *Copy reference*. |
| Upload of a report with an unknown schema version | `400 validation.failed` naming the field. |
| Upload of the same run id twice | Replaces (idempotent), `200`. |
| Compare runs with different datasets | Allowed; the dataset difference is flagged at the top. |

## Non-Functional Requirements

- **Home page:** suggestions + summary in ≤ 300 ms p95 (two queries, no model call); prerendering without double loading (spec 005).
- **Source states:** computed in one query per conversation read (hash lookup on the active versions), ≤ 50 ms p95 for 50 citations.
- **Suggestion generation:** one chat call per version plus ≤ 3 embedding calls; it never delays the version becoming searchable.
- **Search conversations:** `ILIKE` on `unaccent(lower(...))` over the caller's titles and question texts, with an index if needed at planning; ≤ 200 ms p95 for 1,000 conversations.
- **Export:** streamed, ≤ 1 s for 200 messages.
- **Observability:** `documents.suggestions` activity and counter (generated, kept, dropped, failed); Evaluation module logs and activity per upload.
- **Dependencies:** no new library; the Evaluation module uses the existing stack.

## Integrations

- **Chat model** (through `IChatClient`, the API's wiring) from the Documents module for suggestions; the deterministic test fake returns scripted questions.
- **Harness (spec 018):** `upload`, automatic upload after `run` and `judge`, `--no-upload`.
- **Playwright smoke tests:** new tests for the home page, suggested-question click, citation state badges, rename, export download, copy with sources, and the keyboard paths.

## Acceptance Criteria

### Home and suggestions

- **AC-1** Given an indexed version and a fake chat client returning 3 questions of which 2 retrieve the document confidently, then the version stores those 2 with `ready`.
- **AC-2** Given the chat model failing, then the version is indexed and searchable, and `suggestionsStatus = failed` with a code.
- **AC-3** Given demo mode, then `suggestionsStatus = skipped` and no chat call is made.
- **AC-4** Given another user's private document with suggestions, when a knowledge seeker calls `GET /api/documents/suggestions`, then none of its questions are returned; its owner gets them.
- **AC-5** Given the owner, when `PUT …/suggestions` with 2 questions and a valid `If-Match`, then they are stored with `edited` and survive a reprocess; a stale `If-Match` → `412`; a non-owner → `404`.
- **AC-6** Given 4 questions or one longer than 200 characters, then `400 validation.failed`.
- **AC-7** Given readable documents in `en` and `fr`, then the summary counts each language and lists the 5 most recent.
- **AC-8** Given the home page, when a suggested question is clicked, then a new conversation opens and the question is asked (bUnit + smoke).
- **AC-9** Given an empty new conversation, then suggested questions are shown, and clicking one asks it in that conversation.

### Citations

- **AC-10** Given an answer citing version 1 and version 2 now active with an identical passage, then the citation state is `newer-unchanged` and the badge reads *Newer version*.
- **AC-11** Given version 2 without a matching passage, then `newer-changed`, badge *Changed in newer version*, and SourcePreview shows "This source has changed since the answer" and *Open current version*.
- **AC-12** Given the document archived, then `archived` and badge *Archived*.
- **AC-13** Given the document made private by another owner, then `unavailable`.
- **AC-14** Given a citation of the active version, then `current` and no badge.

### Chat conveniences

- **AC-15** Given the owner, when `PATCH` with a title of 50 characters, then the list and the page show it; with whitespace, the generated title returns; with 201 characters, `400`.
- **AC-16** Given another user's conversation, then `PATCH`, export and get return `404`.
- **AC-17** Given conversations with questions "Congés payés" and "Travel budget", when searching `q=conges`, then only the first is returned.
- **AC-18** Given a conversation with a complete, a refused and a partial answer, when exporting, then the Markdown contains every question, the answers with their statuses, a numbered source list per answer with source states, and `Content-Disposition: attachment`.
- **AC-19** Given an answer with 2 citations, when *Copy answer with sources* is clicked, then the clipboard holds the answer text and a 2-item source list; a blocked clipboard shows the fallback field (smoke).

### Evaluation module

- **AC-20** Given a maintainer, when the harness uploads a report, then `201`; uploading the same run id again → `200` and one run.
- **AC-21** Given a knowledge seeker or content owner, then every `/api/admin/evaluation` endpoint returns `403`; anonymous → `401`.
- **AC-22** Given two runs, when comparing, then each metric's delta is returned and differing configuration keys are flagged.
- **AC-23** Given a report whose scope is not the `sample` tag, then `400 validation.failed`.
- **AC-24** Given a maintainer, when deleting a run, then `204` and it no longer appears; the admin tab asks for confirmation (bUnit).
- **AC-25** Given the admin *Evaluation* tab, then runs are listed with headline metrics, and a run detail shows metrics per category and language.

### Accessibility and README

- **AC-26** Given the smoke tests, then Journeys 1–3 complete with the keyboard only, focus returns to the trigger after each dialog or panel, and every citation, badge and icon-only button has an accessible name in the accessibility snapshot.
- **AC-27** Given the manual checklist (contrast in both themes, NVDA reading a citation and an answer status), then each item is recorded as passed in this spec's *Verification* section.
- **AC-28** Given a fresh clone and the README, when following it, then the app runs, `seed` loads the sample corpus, and the demo script works in French and English.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Index for conversation search | Sequential scan with `unaccent` + `lower` | p95 above 200 ms at the target volume |
| Suggestions per document beyond 3 | 3 | Owners ask for more |
| Retention of evaluation runs | Kept until a maintainer deletes them | Storage matters |

## Open Questions

None.

## Implementation notes

- **Suggestions run in the ingestion worker, after the commit** that makes the version searchable (`Suggestions/SuggestionGenerator`), so a slow model delays the next run on that worker slot, never the version. A host that stops mid-step leaves the version `pending`; startup marks it `failed (interrupted)`, and reprocessing runs the step again. Without a chat client, or with `Documents:Suggestions:Enabled = false` (the API sets it in demo mode), the version is `skipped` and the stage log gets no `suggestions` event. The detail page polls the document while its suggestions are pending, because the ingestion stream is done with the page at `completed`.
- **The text hash** is `PassageTextHash` in the shared kernel: NFC, runs of ASCII whitespace collapsed to one space, trimmed, SHA-256. Only ASCII whitespace is collapsed so that both migrations can backfill existing passages and snapshots in SQL with exactly the same rule (`CitationSourceStateTests` checks the SQL expression against the code).
- **`IDocumentSources` reads documents the viewer can no longer read**, to call them `unavailable`. It applies the visibility rule inline and returns only states; `VisibilityRuleCoverageTests` lists it as a state-only exception.
- **The Evaluation module reads the report as JSON** and binds none of the harness's types; metrics are stored as `jsonb`, so their keys come back in PostgreSQL's order, not the harness's. The upload strips the report's corpus passages (only the offline sweep and judge need them). The harness uploads with the maintainer dev user; a failed upload only warns, since the report is on disk.
- **The empty state of a new conversation** falls back to `Chat:SuggestedQuestions` (configuration) when no document has suggestions, for example in demo mode; a click asks the question in both cases.
- **The export link is a plain `<a download>`**: a Fluent anchor button's inner link has no `download` attribute, and Blazor's router then treats the click as a page change (found by the smoke test).

## Verification

**2026-10-20.** `dotnet build` with 0 warnings, `dotnet format --verify-no-changes` clean, `dotnet test --solution RagAssistant.slnx` 1,348 passed (new: `SuggestedQuestionsTests`, `CitationSourceStateTests`, `ConversationConveniencesTests`, `EvaluationRunsTests`, the Evaluation module's boundary test, `ExportSlugTests`, and the bUnit `SourceStateAndCopyTests`, `ChatConveniencesUiTests`, `EvaluationTabTests`, `HomeAndSuggestionsUiTests`). The 21 Playwright smoke tests passed against the AppHost in `https-demo` (new: `FirstReleaseSmokeTests`), and the new migrations applied to the existing dev database, hash backfills included.

| Criteria | How | Result |
|---|---|---|
| AC-1 to AC-7 | Integration tests (fake chat client and scripted vectors), bUnit for the panel and the home page | Pass |
| AC-8 | bUnit (`?ask=` asks once in a new conversation) and smoke (`AskLink_opensANewConversationAndAsksOnce`) | Pass. In demo mode no question is generated, so the click on a *generated* suggestion was not seen live; the link it uses was |
| AC-9 | bUnit | Pass |
| AC-10 to AC-14 | Integration tests per state; bUnit for badges and the preview; smoke for *Newer version* after a reprocess | Pass |
| AC-15 to AC-18 | Integration tests; bUnit; smoke for rename by keyboard and the Markdown download | Pass |
| AC-19 | bUnit (format, blocked clipboard); smoke (real clipboard) | Pass |
| AC-20 to AC-24 | Integration tests; bUnit for the confirmation; two real harness reports uploaded with `upload`, one with its judge result, and uploaded again (replaced) | Pass |
| AC-25 | bUnit | Pass |
| AC-26 | Smoke: keyboard paths through the home page, asking, opening a citation (Escape returns focus, existing `ChatSmokeTests`), rename (focus returns to *Rename*), the document lifecycle and import dialogs (existing smoke tests); accessible names of citations from Playwright's snapshot, of Fluent buttons (*Copy reference*, icon-only *Delete conversation*) from Chromium's own accessibility tree, which Playwright's snapshot does not read for them | Pass |
| AC-27 | Manual checklist: contrast of the new badges in light and dark, NVDA reading a citation and an answer status | **Not done yet.** The badges use Fluent status tokens and carry text and a glyph; the checklist needs a person with NVDA |
| AC-28 | README rewritten (prerequisites, GPU note, the three profiles, users, `seed`, a French and English demo script, tests, smoke tests, harness) | **Not verified from a fresh clone.** The steps were run on this machine's existing clone only |
