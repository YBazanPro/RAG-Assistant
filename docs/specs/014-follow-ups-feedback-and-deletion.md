# Spec 014: Follow-up Questions, Feedback and Conversation Deletion

**Status:** Implemented (2026-10-16). Approved 2026-10-15. `RewriteUsed` is true only when the rewrite result was used (a fallback leaves it false with a reason). The manual rewrite-quality check on the real chat model (the 012 calibration extension) is not done yet
**Date:** 2026-10-15
**Roadmap:** steps 3.5 and 3.6 ([roadmap](../roadmap.md)), plus conversation deletion moved here from spec 012 · **Builds on:** specs [012](012-chat-conversations-and-grounded-answers.md) (chat API, pipeline, traces), [013](013-chat-ui.md) (Chat UI) · **Decision records:** [ADR 0001](../adr/0001-ui-ai-streaming-stack.md), [ADR 0004](../adr/0004-document-versioning.md) (D4: users can delete their own conversations)

## Problem

After specs 012 and 013, every question is answered on its own, so a follow-up such as "Who approves it above that amount?" retrieves nothing useful (`product.md`, Conversation example). Seekers also have no way to say an answer was wrong or helpful, so Phase 4 and 5 would have no user signal. And users cannot delete their own conversations, although ADR 0004 D4 promises that they can. This spec closes Phase 3 under three constraints:

- the conversation must never become evidence (`product.md` challenge 6);
- feedback must attach to the exact answer it judges;
- deletion must remove the user's data, traces included.

## Scope

### In

- **3.5 Follow-ups:**
  - a bounded history window;
  - rewriting into a standalone retrieval query with `GetResponseAsync<RewriteResult>`, with a fallback to the original question;
  - the rewritten query is stored, streamed and shown as "Searched for: …" when it differs from the question.
- **3.6 Feedback:**
  - a rating (Helpful or Not helpful: one per answer, changeable, removable);
  - issue reports with a category and an optional comment, at most 10 per answer.
- **Deleting a conversation:** a hard delete of the conversation, its messages, snapshots, ratings, reports and traces. API and UI.
- **Additive changes to spec 012:** `retrieval.started { query }`, new fields on the user message and the trace, `rating`/`reportCount` on assistant messages in `GET /{id}`, and the pipeline step for the rewrite.
- **UI additions to spec 013:** `AnswerFeedback`, a report dialog, the "Searched for" line, and delete from the list and the thread header.
- **Demo mode:** the rewrite returns the question unchanged.
- The `rag-pipeline` skill is updated with the rewrite pattern and its fakes.

### Out

- History in the **answer** prompt. History is used only by the rewrite; the answer prompt stays as in spec 012.
- Summarizing older history; history beyond the window.
- A separate, smaller rewrite model (deferred).
- Reading ratings and reports (Phase 4 admin); using them in evaluation (Phase 5).
- Editing or deleting an issue report.
- Soft delete, undo, or bulk deletion of conversations.
- Deleting all of a user's data at once (account-level).

## Domain Model

### Follow-up rewriting

- **When:** the conversation has at least one earlier assistant message with status `complete` or `partial`. Otherwise the question is used as is (`RewriteUsed = false`).
- **History window** (`Chat:FollowUp:HistoryExchanges`, default 3): the last N exchanges **before** the current question, oldest first.
  - Each exchange is a user question plus its answer.
  - A `complete` or `partial` answer contributes its text, with `[n]` markers removed and cut to `MaxAnswerChars` (default 1,000).
  - A `refused`, `failed` or `cancelled` answer contributes only its question.
- **Call:** one `IChatClient.GetResponseAsync<RewriteResult>` with `RewriteResult(string StandaloneQuestion)`, bounded by `RewriteTimeout` (default 10 s).
  - The prompt is built in the same prompt module as the answer prompt, with its own `RewritePromptVersion`.
  - It tells the model to resolve references ("it", "that amount") from the history, to keep the user's intent, and to return the question **unchanged** when it already stands alone or starts a new topic.
  - It must not add facts from the history's answers beyond what the reference needs.
- **Validation:** `StandaloneQuestion` is trimmed and must be 1–1,000 characters (the `IDocumentSearch` limit).
- **Fallback:** a timeout, a model error or an invalid result means the **original question** is used. The trace records `RewriteFallbackReason` = `timeout` \| `model_error` \| `invalid`. The answer never fails because of the rewrite; only the caller's cancellation stops it.
- **Use:**
  - retrieval uses `RetrievalQuery` (the standalone question, or the original);
  - the answer prompt contains the numbered passages, the standalone question, and the user's original wording;
  - history never reaches the answer prompt.

### Pipeline change (spec 012, step 3)

The rewrite runs after `question.accepted` and before `retrieval.started`. The `retrieval.started` event now carries `{ query }`, the `RetrievalQuery`. A retry runs the rewrite again, and its `RetrievalQuery` replaces the stored one.

### New and changed fields

| Where | Field |
|---|---|
| User message | `RetrievalQuery` (string). Equals the question when no rewrite happened or it fell back |
| `RequestTrace` | `RetrievalQuery`, `RewriteUsed`, `RewriteFallbackReason?`, `RewriteMs?`, `RewriteInputTokens?`, `RewriteOutputTokens?`, `RewritePromptVersion?` |

### Feedback

| Table | Fields | Rules |
|---|---|---|
| `chat.message_ratings` | `MessageId` (PK, FK to the assistant message), `Rating` (`helpful` \| `not_helpful`), `UpdatedAt` | One per answer. PUT replaces it, DELETE removes it |
| `chat.issue_reports` | `Id`, `MessageId` (FK), `Category` (`incorrect` \| `unsupported_by_sources` \| `wrong_source` \| `outdated` \| `other`), `Comment?` (≤ 1,000 characters, trimmed, empty → `null`), `CreatedAt` | At most 10 per answer. Append-only |

- Feedback is allowed only on assistant messages with status `complete`, `partial` or `refused`. Only the conversation's owner can give it.
- A retry applies only to `failed` or `cancelled` answers, which cannot have feedback, so an answer's feedback never refers to content that changed. The retry still clears any rating or reports on that message, as a defensive rule.

### Deletion

- `DELETE` removes the conversation, all its messages, citation snapshots, ratings, issue reports and request traces in **one transaction**, as a hard delete. Traces hold the question text, so keeping them would defeat the deletion. Maintainers lose diagnostics for deleted conversations; this is accepted.
- Blocked by a **live** `pending` answer (`409 chat.answer_in_progress`). A stale `pending` answer (spec 012 age rule) is swept first and does not block.

### Options — `Chat:FollowUp`

| Option | Default | Rule |
|---|---|---|
| `HistoryExchanges` | 3 | `1..10` |
| `MaxAnswerChars` | 1,000 | ≥ 100 |
| `RewriteTimeout` | 10 s | > 0 and < `Chat:Generation:TotalTimeout` |

`Chat:Feedback:MaxReportsPerAnswer` defaults to 10 and must be ≥ 1.

## API Contract

All routes are under `/api/chat`, with the `KnowledgeSeeker` policy plus ownership. Another user's or a missing conversation or message returns `404` (`chat.conversation_not_found` / `chat.message_not_found`), as in spec 012.

| Verb | Route | Request | Success | Errors |
|---|---|---|---|---|
| DELETE | `/conversations/{id:guid}` | — | `204` | 401 · 403 · 404 · 409 `chat.answer_in_progress` |
| PUT | `/conversations/{id:guid}/messages/{messageId:guid}/rating` | `{ "rating": "helpful" }` | `200 { rating, updatedAt }` | 400 `validation.failed` · 401 · 403 · 404 · 409 `chat.feedback_not_allowed` |
| DELETE | `/conversations/{id:guid}/messages/{messageId:guid}/rating` | — | `204` (also when there was no rating) | 401 · 403 · 404 · 409 `chat.feedback_not_allowed` |
| POST | `/conversations/{id:guid}/messages/{messageId:guid}/reports` | `{ "category": "incorrect", "comment": "…" }` | `201 { id, category, comment, createdAt }` | 400 · 401 · 403 · 404 · 409 `chat.feedback_not_allowed` / `chat.report_limit` |

`chat.feedback_not_allowed` covers a user message, or an answer that is `pending`, `failed` or `cancelled`.

### Changes to spec 012 shapes (additive)

- **`retrieval.started`:** `{ "query": "Who approves a budget exception above 10,000?" }`.
- **User messages in `ConversationDetail`:** `"retrievalQuery": "…"`.
- **Assistant messages in `ConversationDetail`:** `"rating": "helpful" | "not_helpful" | null` and `"reportCount": 0`.

## Authorization

- Same as spec 012: `KnowledgeSeeker` plus ownership by `sub`, with `404` for any other user, maintainers included.
- Only the owner rates, reports or deletes.
- Maintainers will read reports in Phase 4 through admin endpoints that are not defined here.

## Edge Cases & Failure Modes

| Case | Behavior |
|---|---|
| First question of a conversation | No rewrite call; `RetrievalQuery` = the question |
| Only refused or failed answers so far | No rewrite call (no complete or partial answer yet) |
| The model returns the question unchanged | `RewriteUsed = true`; no "Searched for" line, because the text is equal after trimming |
| The rewrite times out, errors, or returns empty or > 1,000 characters | Fall back to the original; the trace has the reason; the answer continues |
| The user cancels during the rewrite | The rewrite call is cancelled and the answer becomes `cancelled` (spec 012 rule) |
| A new topic in the same conversation | The rewriter returns it unchanged (prompt rule). A **New conversation** is always one click away (spec 013) |
| The history answer was wrong | It reaches only the rewrite, never the answer prompt; the answer is grounded in fresh passages |
| Rating a refused answer | Allowed |
| A second PUT with the same rating | `200`, idempotent; `updatedAt` changes |
| An 11th report | `409 chat.report_limit` |
| Comment of 1,001 characters, or an unknown category | `400 validation.failed` |
| Deleting while another tab streams | `409 chat.answer_in_progress`; the UI shows "An answer is being written in another tab or window." |
| Deleting a conversation that is open in another tab | That tab's next call gets `404`, and it shows spec 013's "Conversation not found" view |
| Two concurrent DELETEs | One `204`, the other `404` |

## Non-Functional Requirements

- **Latency:** the rewrite adds a model call before retrieval, so follow-ups may miss the < 3 s time-to-first-token target with a local model. This is accepted and measured (`RewriteMs`, `FirstTokenMs`). The spec 012 calibration spike also records the rewrite latency and quality on 3–4 follow-up pairs, including the `product.md` "it" example.
- **Logging:** the history window and the rewritten query are never logged at Information level.
- **Volume:** ratings ≤ 1 per answer and reports ≤ 10 per answer. Indexes: `issue_reports (message_id)`. The deletion cascade is bounded by `MaxMessagesPerConversation` (200).

## Integrations

- **Chat module only.** No new cross-module contract; retrieval still goes through `IDocumentSearch`.
- **Demo `IChatClient`:** a structured `RewriteResult` request returns the question unchanged.
- **Tests:**
  - the scripted fake `IChatClient` answers the rewrite call separately from the answer call (by response format or by a marker in the prompt);
  - it can delay, throw, or return invalid JSON for the rewrite.
- **Web.Client:**
  - `IChatApi` gains `DeleteConversationAsync`, `SetRatingAsync`, `ClearRatingAsync` and `ReportIssueAsync`;
  - `AnswerFeedback` and the report dialog live in `Web.Client/Chat/`;
  - delete uses the shared `ConfirmDialog`.
- **`rag-pipeline` skill:** add the rewrite (when, window, fallback, fakes).

### UI (additions to spec 013)

- **"Searched for: <query>"**: a quiet line under the user's question when `RetrievalQuery` differs from the question (trimmed, case-sensitive). It is shown live from `retrieval.started` and from `retrievalQuery` when loaded.
- **`AnswerFeedback`** under `complete`, `partial` and `refused` answers, once final:
  - **Helpful** and **Not helpful** toggle buttons (`aria-pressed`). Pressing the active one clears the rating.
  - **Report issue** opens a `FluentDialog` with a category select (required), a comment (optional, with a counter) and Submit/Cancel. After submitting, it shows "Thanks, reported." for that answer, and it stays usable until the limit.
  - Errors show inline next to the control, never as a page error.
- **Delete:**
  - a per-item menu (**Delete**) in `ConversationList`, and a **Delete conversation** button in the thread header;
  - both open `ConfirmDialog` ("Delete this conversation? This can't be undone.");
  - if this tab is streaming in that conversation, the stream is cancelled first;
  - on `204`: navigate to `/chat` if it was the open conversation, refresh the list, and clear its `sessionStorage` draft;
  - a `409` shows the in-progress notice.
- **Screen readers:** the live region announces "Rated helpful", "Rating removed", "Issue reported" and "Conversation deleted".

## Acceptance Criteria

API: integration tests with fakes.

- **AC-1** Given a conversation whose first question is answered `complete`, when the follow-up "Who approves it above that amount?" is asked, then the fake chat client receives one rewrite call that contains the earlier question and answer (markers removed) and not the current question as history. `IDocumentSearch` is called with the rewrite's `StandaloneQuestion`, `retrieval.started.query` equals it, and the user message stores it as `retrievalQuery`.
- **AC-2** Given the first question of a conversation, or a conversation whose earlier answers are all `refused` or `failed`, when asked, then no rewrite call is made and `retrievalQuery` equals the question.
- **AC-3** Given 5 earlier exchanges and `HistoryExchanges = 3`, then the rewrite prompt contains exactly the last 3. A refused exchange contributes only its question, and an answer longer than `MaxAnswerChars` is cut.
- **AC-4** Given a rewrite that times out, throws, returns an empty `StandaloneQuestion`, or returns more than 1,000 characters, then retrieval uses the original question, the answer completes normally, and the trace has `RewriteFallbackReason` `timeout` / `model_error` / `invalid` respectively.
- **AC-5** Given any follow-up, then the **answer** prompt contains the passages, the standalone question and the original wording, and contains no text from earlier answers.
- **AC-6** Given a failed follow-up answer, when it is retried, then the rewrite runs again. A different `StandaloneQuestion` replaces `retrievalQuery`, and the second trace attempt records it.
- **AC-7** Given a cancel during the rewrite (held fake), then the rewrite call observes cancellation and the answer is `cancelled`.
- **AC-8** Given a `complete` answer, when its owner PUTs `helpful`, then `200`, and `GET /{id}` shows `rating: helpful`. A PUT of `not_helpful` replaces it, and DELETE clears it (`204`, and again `204`).
- **AC-9** Given a `refused` answer, rating is allowed. Given a user message, or a `failed`, `cancelled` or `pending` answer, then PUT rating and POST report return `409 chat.feedback_not_allowed`.
- **AC-10** Given an answer with 10 reports, when an 11th is posted, then `409 chat.report_limit`. Given a comment of 1,001 characters or category `spam`, then `400 validation.failed`.
- **AC-11** Given another user's conversation, then DELETE, rating and report all return `404` and change nothing.
- **AC-12** Given a conversation with answers, snapshots, a rating, reports and traces, when its owner DELETEs it, then `204`, and no row of it remains in any `chat` table, including `request_traces`. Other conversations are untouched.
- **AC-13** Given a live `pending` answer, then DELETE returns `409 chat.answer_in_progress`. Given a `pending` answer older than the stale age, then DELETE succeeds.
- **AC-14** Given Demo mode, then a follow-up's rewrite returns the question unchanged and the answer streams normally. A manual check covers this.

UI: bUnit, unless marked Smoke.

- **AC-15** Given a `retrieval.started{query}` that differs from the typed question, then "Searched for: <query>" appears under the question during the stream, and again after a reload (from `retrievalQuery`). When they are equal, no line is shown.
- **AC-16** Given a final `complete` answer, then Helpful and Not helpful are shown with `aria-pressed` reflecting the rating. Pressing Helpful calls `SetRatingAsync`, and pressing it again calls `ClearRatingAsync`. No feedback controls are shown on `failed`, `cancelled` or streaming answers.
- **AC-17** Given Report issue, then the dialog requires a category. Submitting calls `ReportIssueAsync` and shows "Thanks, reported.", and a `409 chat.report_limit` shows an inline message.
- **AC-18** Given Delete from the list item menu or the thread header, then `ConfirmDialog` appears. Confirming calls DELETE, navigates to `/chat` (for the open conversation), removes the item from the list and clears the draft. Cancelling the dialog changes nothing.
- **AC-19** Given a conversation streaming in this tab, when it is deleted, then the stream is cancelled before DELETE is sent.
- **AC-20 (Smoke)** Ask a question, then a follow-up with "it", in Demo mode. Rate the answer Helpful by keyboard. Delete the conversation through the confirm dialog by keyboard. Focus moves sensibly at each step, and the list no longer shows the conversation.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Separate small rewrite model | The same chat model | Follow-up latency in the trace well above target, or Phase 5 shows rewrite quality is fine with a smaller model |
| Summarizing older history | Fixed window of 3 exchanges | Phase 5 cases that need older context |
| Admin view of ratings and reports | Stored only | Phase 4.2 |
| Feedback in evaluation | Not used | Phase 5 |
| Account-level data deletion | Per-conversation only | A real deployment (GDPR-like requirement) |
| Keeping anonymized traces after deletion | Traces are deleted with the conversation | A need for aggregate diagnostics over deleted data |

## Open Questions

None.
