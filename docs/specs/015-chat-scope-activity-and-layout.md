# Spec 015: Chat Scope, Activity Panel and Layout

**Status:** Approved (2026-10-16)
**Date:** 2026-10-16
**Roadmap:** new step 3.7 ([roadmap](../roadmap.md)) · **Builds on:** specs [011](011-retrieval.md) (retrieval filters), [012](012-chat-conversations-and-grounded-answers.md) (pipeline, traces), [013](013-chat-ui.md) (Chat UI), [014](014-follow-ups-feedback-and-deletion.md) (rewrite, "Searched for") · **Related:** [ADR 0009](../adr/0009-document-visibility.md) (private and shared documents, proposed)

## Problem

Three things hurt in the chat today:

1. Every question searches the whole corpus. A user who knows the answer is in one policy, or in documents tagged `hr`, cannot say so, and unrelated documents can crowd out the right passages.
2. The user cannot see what the assistant did. The only trace of the pipeline is the "Searched for: …" line. Why an answer was refused, or which passages were considered and dropped, is visible only to maintainers through the diagnostic search endpoint.
3. The page is cramped and flat. Content is capped at 1100 px (`app.css`, `max-width: 1100px`), so wide screens waste space, and user and assistant messages are not clearly distinguished.

## Scope

### In

- **Scope selection:** a per-conversation scope made of chosen documents and/or tags, stored on the conversation, applied to every question including follow-ups.
- **Activity panel:** a collapsible panel on each assistant answer listing the pipeline steps with real values. It is live while streaming and available later for stored answers.
- **Layout:** a wider chat page and distinct user and assistant message styling.
- API, persistence, UI, tests and the `rag-pipeline` skill update for the above.

### Out

- Private vs shared documents and tenants: [ADR 0009](../adr/0009-document-visibility.md). This spec must not need it; when it lands, retrieval enforces visibility inside `IDocumentSearch`, below the scope.
- Saved collections, a per-user default scope.
- Showing the model's raw prompt or any model "reasoning". The pipeline is a fixed sequence (ADR 0001); the panel shows only recorded facts.
- Maintainer-only trace views and the admin inspector (Phase 4).
- Changing the global page width of other pages (Documents, Admin).
- Dark/light theme redesign; message styling uses the existing Fluent tokens.

## Domain Model

### Scope

`Conversation` gains `ScopeDocumentIds` (uuid[], max 25) and `ScopeTags` (text[], max 20, the retrieval limit). Both empty means the whole corpus (today's behavior).

Combination: if `ScopeDocumentIds` is non-empty, only those documents are searched; `ScopeTags` (all-of, as in spec 011: a document must carry every chosen tag) then narrow within them. Per-question `language`/`tags` on `POST …/messages` stay as they are for API callers; when a request sends `tags`, they replace the scope's tags for that question (the UI never sends them). If only tags are set, they filter the whole corpus. Language filtering is unchanged.

`SearchFilters` (SharedKernel contract) gains `DocumentIds` (`IReadOnlyList<Guid>?`). `IDocumentSearch` applies it in the same query as the existing filters and still returns only active, indexed versions.

Stale scope: a chosen document that is archived, deleted, or has no indexed version is silently excluded from retrieval. The conversation keeps the id; the scope chip shows "unavailable" and the activity panel lists it under "Left out". If every chosen document is unavailable, retrieval returns `NoEvidence` and the answer is refused as usual (no wider fallback: a scope never widens itself).

The scope in force is copied to the request trace (`FilterDocumentIds` beside the existing `FilterTags`), so a stored answer shows the scope it used even after the conversation's scope changes. Changing the scope affects only later questions.

### Activity steps

Each answer exposes ordered steps, built from data already recorded in `RequestTrace`:

| Step | Shown values | Source |
|---|---|---|
| Question | the text asked | trace `QuestionText` |
| Rewrite | used or not, resulting query, fallback reason, ms | `RewriteUsed`, `RetrievalQuery`, `RewriteFallbackReason`, `RewriteMs` |
| Scope | documents and tags applied; documents left out | `FilterDocumentIds`, `FilterTags`, `FilterLanguage` |
| Retrieval | passages considered with document title, section, score, rank, whether cited; thresholds; evidence outcome; ms | `RetrievedPassages`, `MinScore`, `ConfidentScore`, `EvidenceOutcome`, `RetrievalMs` |
| Answer | model called or not, first-token ms, total ms, status, stripped-marker count, sentinel veto | `ChatModel`, `FirstTokenMs`, `TotalMs`, `FinalStatus`, `StrippedMarkerCount`, `SentinelHit` |

Not shown: passage text (the existing source preview does that), prompt text, token counts, model names, OpenTelemetry trace id. Titles come from the cited snapshot or a lookup the owner is already allowed to make; a passage whose document is no longer readable by the user shows "Document unavailable" (relevant once ADR 0009 lands).

### Layout

- The chat page content area is full width up to `--chat-max-width` (default 1600 px), replacing the 1100 px cap on this page only. The message column inside the thread is capped at a readable measure (about 80 characters, `--chat-message-max-width`) and centered; the conversation list and source preview keep their panes.
- **User messages:** right-aligned, tinted bubble using an accent-subtle background token, no avatar.
- **Assistant messages:** left-aligned on the neutral background, with a small assistant label/icon, citations and feedback below.
- Both carry a visually hidden role label ("You said", "Assistant said") for screen readers; role is never conveyed by color alone.
- Below 1024 px the layout is a single column as today.

## API Contract

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| `PUT` | `/api/chat/conversations/{id}/scope` | `{ documentIds: Guid[], tags: string[] }` | `200` conversation scope `{ documentIds, tags, unavailableDocumentIds }` | 400 validation (limits), 404 not owner |
| `GET` | `/api/chat/conversations/{id}` | unchanged | gains `scope` | |
| `GET` | `/api/chat/conversations/{id}/messages/{messageId}/activity` | none | `200 { question, rewrite, scope, retrieval, answer }`, one named object per step, for the latest attempt | 404 not owner or no trace |
| SSE | `POST …/messages` | unchanged | `retrieval.completed` gains `scope`, `leftOut`, `minScore`, `confidentScore`, `retrievalMs`, and a `score` on each source; stage events already exist | |

- `PUT scope` replaces the scope (idempotent). It validates that each document id exists and is readable by the caller; unknown ids give `400` with the offending ids, so a stale link cannot silently build an empty scope.
- Reuse `GET /api/documents` (spec 008) for the picker's search and the facets endpoint for tags: no new Documents endpoints. The picker needs a title search parameter if one is not already there (check during planning; adding `q` to the list is allowed).
- The activity endpoint returns the same step shape the live stream builds, so the UI has one renderer. It is owner-only (404 for others, like the rest of Chat).
- Policy: `KnowledgeSeeker` for all of the above.

## Authorization

Everyone who can chat (KnowledgeSeeker and above) can set a scope on their own conversation and read the activity of their own answers. Nobody reads another user's activity through this endpoint; maintainers keep using Phase 4 tools. The picker lists only documents the caller can already browse.

## Edge Cases & Failure Modes

- Two tabs change the scope: last write wins; each question uses the scope read when it starts and records it in the trace.
- A question in flight when the scope changes: it finishes under the scope it started with.
- A scope with 25 documents plus tags still runs one query; the document filter is an `= ANY(@ids)` predicate on the existing query.
- A retry (spec 012) uses the scope stored in its original trace, not the current one, so a retried answer can be explained by its activity.
- Activity for a refused answer shows steps up to retrieval and "Model not called".
- Activity for answers created before this spec has no scope step and shows what the old trace holds; missing fields are omitted, not guessed.
- Deleting a conversation deletes its scope (it is a column) and traces as before.
- Rewrite fallback and timeouts display their stored reasons.
- The panel is announced to assistive technology only when opened; live updates while closed do not interrupt the answer's status announcements.
- Very long titles truncate with a tooltip; passage lists beyond 20 show a "show all" control.

## Non-Functional Requirements

- Scope filter adds no more than 10 ms to retrieval on the dev corpus; the existing HNSW search is unchanged. If a selective filter makes the planner skip the index, the result is still correct, only slower (record in the spec 011 notes if observed).
- The activity endpoint is a single trace lookup by indexed key; no model call, no embedding call.
- Accessibility: the panel is a disclosure widget operable by keyboard; the picker is a dialog with a focus trap and a labelled listbox; contrast meets the existing token rules.
- Smoke test coverage required: scope picker (dialog, focus, keyboard) and the panel disclosure, per the `ui-smoke-tests` skill.

## Integrations

None. Retrieval keeps being reached only through `IDocumentSearch`; Chat never references Documents internals (the picker talks to the Documents HTTP API through the BFF like the Documents UI does).

## Acceptance Criteria

- **AC-1** Given a conversation with no scope, when a question is asked, then retrieval searches the whole corpus as before.
- **AC-2** Given documents A and B are chosen, when a question is asked, then only passages of A and B can be retrieved and cited.
- **AC-3** Given documents A and B and tag `hr` are chosen, when a question is asked, then only passages of A or B that carries tag `hr` can be retrieved; a document in the list without the tag is excluded.
- **AC-4** Given only tag `hr` is chosen, when a question is asked, then the whole corpus is searched filtered by that tag.
- **AC-5** Given the scope was set before a follow-up, when the follow-up is asked, then the rewritten query is searched under the same scope.
- **AC-6** Given a chosen document is archived, when a question is asked, then it is excluded, the chip shows "unavailable", and the activity lists it under "Left out".
- **AC-7** Given every chosen document is unavailable (tags cannot widen a document scope), when a question is asked, then the answer is a refusal and no model call occurs.
- **AC-8** Given the scope changes after an answer, when that answer's activity is opened, then it shows the scope the answer used.
- **AC-9** Given `PUT scope` names an unknown document or one the caller cannot read, then the response is 400 and the scope is unchanged.
- **AC-10** Given a user who is not the owner, when they call `PUT scope` or the activity endpoint, then the response is 404.
- **AC-11** Given an answer is streaming, when the panel is open, then steps appear as their events arrive and the final values match the stored activity.
- **AC-12** Given a stored answer, when the panel is opened, then it shows question, rewrite, scope, retrieval (passages, scores, thresholds, outcome) and answer steps from the trace.
- **AC-13** Given a refused answer, then the activity shows "Model not called" and the refusal reason.
- **AC-14** Given the panel, then it contains no prompt text, no passage text and no token counts.
- **AC-15** Given a viewport of 1920 px, then the chat content uses up to `--chat-max-width` and the message column stays at the readable measure; at under 1024 px it is a single column.
- **AC-16** Given a thread, then user messages are right-aligned in a tinted bubble and assistant messages left-aligned with a label, and each has a screen-reader role label.
- **AC-17** Given the scope picker, when used by keyboard only, then every choice can be made, focus is trapped while open and returns to the opener on close.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Scope limit (25 documents, 20 tags) | those numbers | a user needs more |
| Persisting panel open/closed state | not persisted; closed by default for stored answers, open while streaming | feedback from use |
| Scope by saved collection | not built | users repeat the same scope often |
| Visibility interplay | scope only narrows; `IDocumentSearch` enforces visibility (ADR 0009) | ADR 0009 accepted |

## Open Questions

None.
