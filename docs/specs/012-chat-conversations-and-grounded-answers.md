# Spec 012: Chat — Conversations and Grounded Answers

**Status:** Approved (2026-10-15)
**Date:** 2026-10-15
**Roadmap:** steps 3.1–3.3, plus the write side of 4.1 ([roadmap](../roadmap.md)) · **Builds on:** specs [002](002-shared-kernel-api-conventions.md), [004](004-identity.md), [008](008-browse-corpus-and-documents-ui.md) (paging), [011](011-retrieval.md) · **Decision records:** [ADR 0001](../adr/0001-ui-ai-streaming-stack.md), [ADR 0003](../adr/0003-model-provider.md), [ADR 0004](../adr/0004-document-versioning.md)

Phase 3 is split into three specs: **012** (this one: the backend for 3.1–3.3), **013** (Chat UI, 3.4) and **014** (follow-ups, feedback and conversation deletion, 3.5–3.6).

## Problem

The corpus can be searched (spec 011), but nobody can ask it a question yet. Knowledge seekers need to ask in a conversation and get one of two results:

- an answer that streams in, is grounded in retrieved passages and cites them;
- an honest refusal when the corpus does not support an answer.

The status of each answer (complete, partial, refused, failed) must be explicit in the API, and every citation must stay verifiable after the document changes (ADR 0004). Maintainers will need to explain any answer later (Phase 4), so each request must record its decisions from the start. Requests made before Phase 4 that recorded nothing could never be diagnosed.

## Scope

### In

- **Chat module** `RagAssistant.Modules.Chat` (`/scaffold`), `chat` schema, its own `ChatDbContext`, migrations history and `AddChatPersistence` registered in the MigrationService.
- **3.1 Conversations:** create (empty body), list your own (paged), get one of your own with all its messages.
- **3.2 Ask → refusal:** `POST …/messages` streams SSE. When the evidence outcome is `NoEvidence`, the answer is refused without calling the model.
- **3.3 Grounded answer:**
  - one prompt builder;
  - `[n]` citation markers, validated and stripped in the stream;
  - a model veto through a sentinel;
  - status `complete` or `partial`;
  - citation snapshots;
  - a disconnect cancels the answer;
  - `error` events with `retryable`;
  - a retry endpoint for the last failed or cancelled answer.
- **Request traces, write side** (roadmap 4.1): one `chat.request_traces` row per accepted attempt.
- **Model wiring in the API:**
  - an Ollama chat model in the AppHost;
  - `IChatClient` with `UseOpenTelemetry()` and `UseLogging()`;
  - `Chat:Provider = Ollama | Demo`, where Demo is a deterministic extractive fake (ADR 0003).
- **Chat model calibration spike** during implementation (see Non-Functional Requirements).
- A new project skill `rag-pipeline` (roadmap "Project skills to create", after 3.3). It covers prompt construction, the citation format and its validation, the sentinel, and test fakes.

### Out

- Any UI (spec 013).
- Follow-up questions: history in the prompt and query rewriting. Every question in 012 is answered on its own, with no history (spec 014).
- Feedback (spec 014).
- Deleting a conversation (spec 014, ADR 0004 D4). Until then, conversations cannot be deleted.
- Renaming a conversation.
- Reading traces through endpoints or the admin UI (Phase 4.1/4.2).
- Custom OTel metrics (4.3). Only the spans and metrics that `UseOpenTelemetry()` provides are in scope.
- Streaming resume (`Last-Event-ID`) for answers: a disconnect cancels the answer.
- Conflict surfacing, claim extraction, reranking, history search.
- Caching answers or retrieval results (never: CLAUDE.md, ADR 0008).

## Domain Model

All tables live in the `chat` schema. Nothing references the `documents` schema: the snapshots are copies (ADR 0004).

### `Conversation`

| Field | Rule |
|---|---|
| `Id` | Guid |
| `OwnerSub` | The `sub` claim of the creator, never a username or email |
| `Title` | `null` until the first question. Then it is that question with whitespace collapsed, cut at a word boundary to ≤ 80 characters, with `…` appended when cut. It never changes afterwards |
| `CreatedAt`, `LastActivityAt` | From `TimeProvider`. `LastActivityAt` is updated when a question is accepted and when an answer is finalized |
| `MessageCount` | Kept in sync, so the cap check needs no count query |

### `Message`

One row per message, with a role.

| Field | Rule |
|---|---|
| `Id`, `ConversationId` | |
| `Role` | `user` \| `assistant` |
| `Sequence` | 1-based, unique per conversation, strictly increasing |
| `Text` | User: the question, trimmed, 1–1,000 characters (the same limit as `IDocumentSearch`). Assistant: the answer as streamed, after marker stripping. Empty for `pending`, `refused`, `failed` and `cancelled` |
| `CreatedAt` | |
| `Filters` *(user)* | `language?`, `tags?` as sent |
| `ReplyToMessageId` *(assistant)* | The user message it answers |
| `Status` *(assistant)* | `pending` \| `complete` \| `partial` \| `refused` \| `failed` \| `cancelled` |
| `RefusalReason` *(assistant)* | `no_evidence` \| `model_declined`; set only when the status is `refused` |
| `ErrorCode` *(assistant)* | Set only when the status is `failed` |
| `CompletedAt` *(assistant)* | |

### `CitationSnapshot`

A child of an assistant message, with one row per **cited** passage.

| Field | Rule |
|---|---|
| `Marker` | The `n` used in the text |
| `PassageId`, `DocumentId`, `VersionId`, `VersionNumber` | Copied from `RetrievedChunk` |
| `DocumentTitle`, `SectionPath`, `PageStart`, `PageEnd` | Copied from `RetrievedChunk` |
| `Score` | Copied from `RetrievedChunk` |
| `Text` | The **full** passage text at answer time |

Snapshots are never updated. Citations always render from the snapshot. Marking superseded or archived sources is Phase 6.

### `RequestTrace`

One row per accepted attempt. It holds decision data only: no passage text and no prompt (ADR 0004 D4).

- **Identity:**
  - `Id`, `ConversationId`, `UserMessageId`, `AssistantMessageId`, `Attempt` (1, 2, … per assistant message);
  - `OwnerSub`;
  - `QuestionText`, `Filters`;
  - `OtelTraceId`.
- **Retrieval:**
  - `EvidenceOutcome` and `Thresholds` (from `SearchResult`);
  - `EmbeddingModel`;
  - `RetrievedPassages`, a JSON list with one entry per kept passage: `passageId`, `versionId`, `score`, `rank`, `marker`, `cited`.
- **Generation:**
  - `ChatModel` (`null` when the model was never called);
  - `PromptVersion`;
  - `InputTokens?`, `OutputTokens?` (`null` when the provider reports no usage);
  - `SentinelHit`;
  - `StrippedMarkerCount`.
- **Result:**
  - `FinalStatus`, `RefusalReason?`, `ErrorCode?`;
  - `StartedAt`, `RetrievalMs`, `FirstTokenMs?`, `TotalMs`.

A trace is written for every attempt after `question.accepted`, whatever its outcome, and is kept indefinitely (ADR 0004 D4). Requests rejected before the stream starts (400, 404, 409) write none.

### Answer status rules

| Situation | Status | Model called |
|---|---|---|
| `NoEvidence` | `refused`, `no_evidence` | No |
| Reply starts with the sentinel (any outcome above the floor) | `refused`, `model_declined` | Yes |
| `LowConfidence`, answer given | `partial` | Yes |
| `Sufficient`, answer with ≥ 1 valid citation | `complete` | Yes |
| `Sufficient`, answer with no valid citation | `partial` | Yes |
| Failure after `question.accepted` | `failed` + `ErrorCode` | — |
| Client disconnect | `cancelled`, no text kept | — |

### Pipeline (one handler, fixed order — ADR 0001)

1. **Pre-stream checks**, which return ProblemDetails:
   - validation;
   - the conversation exists and belongs to the caller (`404` otherwise);
   - no live `pending` answer (`409`);
   - the message cap (`409`).
2. **Persist** the user message and an assistant message in status `pending`, set the title if it is the first question, then emit `question.accepted`.
3. Emit `retrieval.started`, then call `IDocumentSearch.SearchAsync(question, filters, Chat:Retrieval:TopK)`.
4. **Number** the kept chunks `[1]..[k]` in rank order, then emit `retrieval.completed` with the sources (empty when `NoEvidence`).
5. **`NoEvidence`:** finalize as `refused/no_evidence`, write the trace, emit `answer.completed`, end the stream.
6. **Build the prompt** (see below) and stream through `IChatClient.GetStreamingResponseAsync`.
7. **Filter the stream:**
   - **Sentinel holdback.** Text is held back while the trimmed reply could still be a prefix of the sentinel. It is released as soon as it can no longer match, so a normal answer's first token is not delayed. If the reply starts with the sentinel (case-insensitive, punctuation allowed after it), the rest of the output is discarded and the answer is finalized as `refused/model_declined`.
   - **Marker filter.** An opening `[` is held back until the bracket closes or can no longer be a marker, capped at 32 characters; past the cap, or once a character other than a digit, a comma or a space appears, the held text is released as ordinary text.
     - A complete `[n]` with `1 ≤ n ≤ k` passes through.
     - A list `[n, m, …]` (numbers separated by commas, with optional spaces) is normalized to `[n][m]…`. The prompt still asks for separate markers.
     - Numbers outside `1..k` are dropped and counted, whether alone or inside a list. A list with no valid number disappears entirely.
     - Any other bracketed text passes through as ordinary text.
   - Every released fragment is sent as `answer.delta` and appended to the answer text.
8. **Finalize:**
   - work out the status from the rules above;
   - snapshot the cited passages (distinct markers that appeared in the text);
   - save the message, the snapshots and the trace in one transaction;
   - emit `answer.completed`.
9. **Cancellation (`RequestAborted`):** stop the model call and finalize as `cancelled`. Use a fresh token for this write, so the disconnect does not cancel it. Write the trace and emit nothing.
10. **Failures** in steps 3–8: finalize as `failed`, write the trace, emit `error`, end the stream.

### Prompt (Chat module, one place)

- A `PromptBuilder` with a `PromptVersion` constant, recorded in the trace.
- The **system** message:
  - answer in English, only from the numbered passages;
  - cite each claim with `[n]`;
  - give no facts beyond the passages;
  - be concise, keeping conditions;
  - plain prose, lists allowed, no headings;
  - if the passages do not answer the question, reply with exactly the sentinel and nothing else.
- For **`LowConfidence`** it adds: the evidence may be incomplete; answer only what the passages support and say what they do not cover.
- The **user** message contains the numbered passages (title, section path, pages, text), then the question.
- No conversation history (spec 014).

### Options

**`Chat:Generation`**, validated on start:

| Option | Default | Rule |
|---|---|---|
| `FirstTokenTimeout` | 30 s | > 0 |
| `TotalTimeout` | 2 min | > `FirstTokenTimeout` |
| `MaxOutputTokens` | 1,000 | ≥ 1 |
| `Sentinel` | `INSUFFICIENT_EVIDENCE` | Non-empty, no whitespace |
| `HeartbeatInterval` | 15 s | > 0 |

**`Chat:Retrieval`:** `TopK` defaults to 8 and must be `1..20` (the Documents `MaxTopK`).

**`Chat:Conversations`:** `MaxMessagesPerConversation` defaults to 200 (100 exchanges); a retry does not add messages.

**`Chat:Provider`:** `Ollama` (the default) or `Demo`.

There are no automatic retries for model calls, before or after the first token. The user retries.

### Stale `pending` answers

- **At API startup**, a hosted service marks every `pending` assistant message `failed` with `chat.interrupted`. It is single-instance, like ingestion.
- **On ask or retry**, a `pending` message older than `TotalTimeout + 1 min` is treated the same way first, so it never holds the `409` forever.

## API Contract

All routes are under `/api/chat` and require the `KnowledgeSeeker` policy and ownership. A conversation that is missing **or** belongs to another user returns `404 chat.conversation_not_found`, for maintainers too.

| Verb | Route | Request | Success | Errors |
|---|---|---|---|---|
| POST | `/conversations` | empty | `201` `ConversationSummary`, `Location` header | 401 · 403 |
| GET | `/conversations` | `page` (default 1), `pageSize` (default 20, max 100) | `200` `Page<ConversationSummary>`, sorted `lastActivityAt` desc, then `id` | 400 `validation.failed` · 401 · 403 |
| GET | `/conversations/{id:guid}` | — | `200` `ConversationDetail` | 401 · 403 · 404 |
| POST | `/conversations/{id:guid}/messages` | `AskRequest` | `200 text/event-stream` | 400 `validation.failed` · 401 · 403 · 404 · 409 `chat.answer_in_progress` / `chat.conversation_full` |
| POST | `/conversations/{id:guid}/messages/{assistantMessageId:guid}/retry` | empty | `200 text/event-stream` | 401 · 403 · 404 `chat.conversation_not_found` / `chat.message_not_found` · 409 `chat.answer_in_progress` / `chat.retry_not_allowed` |

Notes:

- The `Page<T>` envelope is the one from spec 008: `{ items, page, pageSize, totalCount }`.
- POSTs through the BFF need `X-Requested-With: ragassistant` (spec 005); the API itself does not check it.
- **Retry** is allowed only on the conversation's **last** assistant message, and only when its status is `failed` or `cancelled`.
  - It reuses that message's question and filters.
  - It resets the message to `pending` and clears its text, snapshots and error.
  - It streams like an ask, and its `question.accepted` carries the same IDs.
  - It writes a new trace row with `Attempt + 1`.
  - In any other case it returns `409 chat.retry_not_allowed`.

### Shapes (camelCase, enums as lowercase strings)

```jsonc
// AskRequest
{ "question": "Who approves a budget exception?", "language": "en", "tags": ["finance"] }   // language, tags optional (spec 011 rules)

// ConversationSummary
{ "id": "…", "title": "Who approves a budget exception?", "createdAt": "…", "lastActivityAt": "…" }

// ConversationDetail
{ "id": "…", "title": "…", "createdAt": "…", "lastActivityAt": "…",
  "messages": [
    { "id": "…", "role": "user", "sequence": 1, "text": "Who approves…?", "createdAt": "…",
      "filters": { "language": null, "tags": [] } },
    { "id": "…", "role": "assistant", "sequence": 2, "replyToMessageId": "…", "status": "complete",
      "refusalReason": null, "errorCode": null, "retryable": null, "text": "Department head and Finance [1].",
      "createdAt": "…", "completedAt": "…",
      "citations": [ { "marker": 1, "passageId": "…", "documentId": "…", "versionId": "…", "versionNumber": 3,
                       "documentTitle": "Budget Exception Policy", "sectionPath": ["Approval"], "pageStart": null,
                       "pageEnd": null, "score": 0.7134, "text": "…full passage…" } ] } ] }
```

### Answer stream events

| Event | Data | When |
|---|---|---|
| `question.accepted` | `{ conversationId, userMessageId, assistantMessageId }` | After the pending rows are committed |
| `retrieval.started` | `{}` | Before `SearchAsync` |
| `retrieval.completed` | `{ outcome, sources: [{ marker, passageId, documentId, documentTitle, versionNumber, sectionPath, pageStart, pageEnd }] }` | `sources` is empty for `noEvidence` |
| `answer.delta` | `{ text }` | Repeated, already filtered |
| `answer.completed` | `{ status, refusalReason, citations: [CitationSnapshot] }` | Final. `citations` holds the cited sources only |
| `error` | `{ code, message, retryable }` | Final |
| `heartbeat` | empty | Every `HeartbeatInterval` while nothing else has been sent (for example, while the model loads) |

- No event IDs: the stream is not resumable.
- Exactly one terminal event (`answer.completed` or `error`) ends a stream that was not cancelled.
- Clients ignore unknown event types.
- The DTOs follow the `sse-endpoints` skill: serialized with web defaults and yielded as `SseItem<string>`. The endpoint resolves everything that can fail before the stream starts, then returns `TypedResults.ServerSentEvents`.

### Error codes

| Code | Where | Retryable |
|---|---|---|
| `validation.failed` | 400 | — |
| `chat.conversation_not_found`, `chat.message_not_found` | 404 | — |
| `chat.answer_in_progress`, `chat.conversation_full`, `chat.retry_not_allowed` | 409 | — |
| `retrieval.embedding_unavailable` | `error` event | yes |
| `chat.model_unavailable` | `error` event: connection or provider failure | yes |
| `chat.model_timeout` | `error` event: `FirstTokenTimeout` or `TotalTimeout` | yes |
| `chat.interrupted` | Stored on a stale `pending` answer | yes |
| `chat.internal` | `error` event: anything else | no |

On an assistant message, `retryable` is derived from `errorCode` and is `null` unless the status is `failed`. A `cancelled` answer can always be retried.

## Authorization

- Every endpoint requires the `KnowledgeSeeker` policy. Content owners and maintainers hold that role too.
- Ownership is by `sub`. A conversation that belongs to someone else returns `404`, even for a maintainer (`product.md`: "other users cannot see it").
- Maintainers inspect requests through traces in Phase 4, never through these endpoints.
- No anonymous access. The fallback policy applies.

## Edge Cases & Failure Modes

| Case | Behavior |
|---|---|
| Question empty or whitespace, or > 1,000 characters after trimming | `400`, nothing persisted |
| Invalid `language` or `tags` | `400`, using the spec 011 rules, validated before the stream |
| Two questions at once in one conversation (two tabs) | The second gets `409 chat.answer_in_progress`. A row lock on the conversation, taken when the pending rows are inserted, prevents a race |
| Conversation at `MaxMessagesPerConversation` | `409 chat.conversation_full`. The UI suggests a new conversation |
| Embedding model down | `error` `retrieval.embedding_unavailable`, retryable; the message is `failed` |
| Chat model down or not pulled yet | `error` `chat.model_unavailable`, retryable |
| No first token within `FirstTokenTimeout`, or the stream exceeds `TotalTimeout` | `error` `chat.model_timeout`. Deltas already sent stay in the client's view, but the persisted text is empty and the status is `failed` |
| `MaxOutputTokens` reached | The answer is finalized from the text received, and the status rules apply. The truncation is recorded in the trace (`OutputTokens`) |
| Reply is only the sentinel, or starts with it | `refused/model_declined`. No `answer.delta` was sent |
| The sentinel appears later in the reply | Ordinary text |
| `[9]` with `k = 8`, `[0]`, `[]`, `[abc]` | `[9]` and `[0]` are dropped and counted. `[]` and `[abc]` are text |
| A marker split across tokens (`[` then `3]`) | Held back until complete or impossible, then released |
| `[1, 2]`, `[1,2]`, `[2, 9]` with `k = 8` | Sent and saved as `[1][2]`, `[1][2]` and `[2]` (9 counted as stripped) |
| `[1, 2` never closed, or a bracket longer than 32 characters | Released as ordinary text |
| The same passage cited several times | One snapshot |
| Client disconnects mid-stream | The model call is cancelled and the message becomes `cancelled`. The trace is written with a non-cancelled token |
| Process crash with an answer `pending` | Swept to `failed/chat.interrupted` at startup, or by the age check |
| Retry on a non-last, `complete`, `partial`, `refused` or `pending` message | `409 chat.retry_not_allowed` (`pending` → `chat.answer_in_progress`) |
| A cited document is archived or superseded later | The snapshot is unchanged and the citation still renders |
| The provider reports no token usage | The token fields are `null` |

## Non-Functional Requirements

- **Scale:** about 1,000 users, ~100 conversations each and ~10 messages per conversation, which is ~1M messages, a few million snapshot rows and ~500k traces a year. No partitioning. Indexes:
  - `conversations (owner_sub, last_activity_at desc, id)`;
  - `messages (conversation_id, sequence)`, unique;
  - `messages (status)`, partial index where `status = 'pending'`, for the sweep;
  - `citation_snapshots (message_id)`;
  - `request_traces (assistant_message_id)` and `request_traces (started_at)`.
- **Latency:** time to first token < 3 s is a **target** (ADR 0004 D3). It is measured in the trace (`FirstTokenMs`) and never asserted in tests. The refusal path makes no model call.
- **Logging:** no prompt, passage text or answer text at Information level. `UseLogging()` stays at Debug for content.
- **Calibration spike** (during implementation, like spec 011):
  - run the spec 011 calibration questions (8 on-corpus, 6 off-corpus) against 2–3 small instruct models that fit on a laptop (~3–4B, e.g. `qwen3:4b`, `llama3.2:3b`, `gemma3:4b`);
  - criteria: valid `[n]` citations, use of the sentinel on off-corpus questions that still pass the floor, and time to first token on the dev machine;
  - record the chosen model, the questions and the observations in a **Calibration** section of this spec, and set the model in the AppHost.
  - The result stays provisional until Phase 5.

## Integrations

- **Documents:** only through `IDocumentSearch` (SharedKernel contract). The Chat module never references the Documents project, its DbContext or its types beyond the contract.
- **AppHost:**
  - `ollama.AddModel("chat", ChatModel)` next to `embeddings`, and `api.WithReference(chat)`;
  - a launch setting or parameter switches `Chat:Provider` to `Demo`, in which case the chat model is not referenced.
- **API `Program.cs`** (the only place that knows the provider):
  - `Ollama`: `AddOllamaApiClient("chat").AddChatClient().UseOpenTelemetry().UseLogging()`;
  - `Demo`: registers the extractive fake behind the same pipeline. It takes the first sentence of the top 1–2 passages from the prompt, appends `[1]`/`[2]`, streams word by word with a small delay, and never sends the sentinel. Retrieval stays real.
- **Resilience:** the ServiceDefaults HTTP timeouts (2 min per attempt, 5 min in all) must not cut in before `Chat:Generation:TotalTimeout`. The module's own deadlines are the ones that count.
- **Tests:** they use their own scripted fakes, never Demo:
  - a fake `IChatClient` that streams the given tokens, can hold before its first token, and can throw at a chosen point;
  - the existing controlled-vector fake embedding generator.
- **MigrationService:** registers `AddChatPersistence`.
- **Web BFF:** no change; YARP already passes `/api/**` through without buffering (spec 005).

## Acceptance Criteria

Conversations (3.1):

- **AC-1** Given a signed-in seeker, when they `POST /api/chat/conversations`, then `201` with a `ConversationSummary` whose `title` is `null`, a `Location` header, and `ownerSub` = their `sub` in the database.
- **AC-2** Given seeker A with 3 conversations and seeker B with 1, when A calls `GET /api/chat/conversations`, then `200` with `totalCount` 3, only A's conversations, sorted by `lastActivityAt` descending.
- **AC-3** Given B's conversation, when A (or a maintainer) calls `GET`, `POST …/messages` or `POST …/retry` on it, then `404 chat.conversation_not_found`.
- **AC-4** Given no token, then each chat endpoint returns `401`. Given a token without the `knowledge-seeker` role, then `403`.
- **AC-5** Given `pageSize=101` or `page=0`, then `400 validation.failed`.
- **AC-6** Given a conversation with two answered questions, when its owner calls `GET /{id}`, then the messages come back in `sequence` order, and each assistant message has its status and citations, with the full snapshot text.

Ask: validation and concurrency:

- **AC-7** Given a question that is empty, whitespace or 1,001 characters long, or an invalid `language`, when asked, then `400 validation.failed`, no message or trace is persisted, and the response is not `text/event-stream`.
- **AC-8** Given an answer `pending` in a conversation, when a second question is posted, then `409 chat.answer_in_progress`.
- **AC-9** Given a conversation with `MaxMessagesPerConversation` messages, when asked, then `409 chat.conversation_full`.
- **AC-10** Given a new conversation, when the first question is a 120-character question, then the title is cut at a word boundary to ≤ 80 characters and ends with `…`. A second question does not change it.

Refusal (3.2):

- **AC-11** Given a corpus where nothing reaches `MinScore`, when asked, then the stream is exactly `question.accepted` → `retrieval.started` → `retrieval.completed{outcome: noEvidence, sources: []}` → `answer.completed{status: refused, refusalReason: no_evidence, citations: []}`, the fake chat client received **no** call, and the message is persisted as `refused` with empty text.
- **AC-12** Given AC-11, then one trace row exists with `EvidenceOutcome` `NoEvidence`, `ChatModel` `null`, `FinalStatus` `refused` and no passage text.

Grounded answer (3.3):

- **AC-13** Given `Sufficient` evidence with 3 kept passages and a fake model that streams `"Department head [1] and Finance [3]."` in several tokens, then:
  - `retrieval.completed` lists markers 1–3 before any `answer.delta`;
  - the concatenated deltas equal the text;
  - `answer.completed` has status `complete` and citations for markers 1 and 3 only;
  - the persisted text and snapshots match, and each snapshot's `text` equals the passage text.
- **AC-14** Given `k = 3` and a model that streams `"Rule [1] and [7]."` with the marker split as `"[" "7]"`, then the deltas concatenate to `"Rule [1] and ."`, `StrippedMarkerCount` = 1, and only marker 1 is snapshotted.
- **AC-14b** Given `k = 3` and a model that streams `"Both apply [1, 3] and [2,7]."` with the list split across tokens, then the deltas concatenate to `"Both apply [1][3] and [2]."`, `StrippedMarkerCount` = 1, and markers 1, 2 and 3 are snapshotted.
- **AC-15** Given `LowConfidence` evidence and a model answer with valid citations, then the status is `partial`, and the prompt the fake received contains the low-confidence instruction.
- **AC-16** Given `Sufficient` evidence and a model answer with no valid marker, then the status is `partial` and `citations` is empty.
- **AC-17** Given a model whose reply is `"INSUFFICIENT_EVIDENCE."`, streamed as `"INSUFF" "ICIENT_EVIDENCE."`, then no `answer.delta` is sent, `answer.completed{status: refused, refusalReason: model_declined}`, the persisted text is empty and the trace has `SentinelHit` = true.
- **AC-18** Given a normal answer whose first token is `"Department"`, then the first `answer.delta` is sent without waiting for more tokens: the holdback releases once the text cannot be the sentinel.
- **AC-19** Given a prompt built for any question, then it contains each kept passage with its marker, title, section path and text, the sentinel instruction, and no earlier message of the conversation.

Failures, cancellation and retry:

- **AC-20** Given an embedding generator that fails, when asked, then `question.accepted` → `retrieval.started` → `error{code: retrieval.embedding_unavailable, retryable: true}`, the message is `failed` with that code, and a trace is written.
- **AC-21** Given a fake model that holds before its first token longer than `FirstTokenTimeout` (moved by a fake `TimeProvider` or a short option), then `error{code: chat.model_timeout, retryable: true}` and status `failed`.
- **AC-22** Given a fake model that throws after two tokens, then the client got those two deltas, then `error{code: chat.model_unavailable, retryable: true}`. The persisted text is empty and the status is `failed`.
- **AC-23** Given a held fake model, when the client cancels the request after `retrieval.completed`, then the fake observes cancellation, the message becomes `cancelled` with empty text, and a trace with `FinalStatus` `cancelled` exists.
- **AC-24** Given the last assistant message is `failed`, when its owner calls `retry`, then the stream's `question.accepted` carries the same message IDs, a successful answer replaces the message content, the conversation's message count is unchanged, and a second trace row with `Attempt` 2 exists.
- **AC-25** Given the last assistant message is `complete`, or a `failed` message that is not the last, when `retry` is called, then `409 chat.retry_not_allowed`.
- **AC-26** Given `pending` assistant messages in the database when the API starts, then after startup they are `failed` with `chat.interrupted`. Given a `pending` message older than `TotalTimeout + 1 min`, when a new question is asked, then it is accepted (no `409`) and the old message is `failed/chat.interrupted`.
- **AC-27** Given a fake model that holds longer than `HeartbeatInterval` before its first token, then at least one `heartbeat` event arrives before the first `answer.delta`.

Provenance and wiring:

- **AC-28** Given a completed answer that cites a passage, when that document is archived and then a new version is ingested, then `GET /{id}` still returns the original snapshot (same text, version number and title).
- **AC-29** Given `Chat:Provider = Demo`, then the app starts with no chat model registered with Ollama, and an on-corpus question streams an answer that cites `[1]` and ends `complete`. A manual check covers this; it is not run in `dotnet test`.
- **AC-30** `get_project_graph` shows no reference from `RagAssistant.Modules.Chat` to `RagAssistant.Modules.Documents` or the reverse, and no provider package is referenced from the Chat module.
- **AC-31** The MigrationService applies the `chat` schema migration, and the integration fixture's `DatabaseMigrator` creates it.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Default chat model | Chosen by the calibration spike during implementation and recorded here | Phase 5 evaluation, or a better small model |
| Deleting conversations | Not possible until spec 014 | Spec 014 |
| Trace retention | Kept indefinitely (ADR 0004 D4) | Any real deployment, or > ~10k users (see the scale note) |
| Partitioning traces and snapshots | None at the ~1M-message scale | Volume beyond ~10M messages |
| Rendering the answer text (Markdown subset) | Stored raw; spec 013 decides how to render it safely (no raw HTML) | Spec 013 |
| Superseded/archived badges on citations | Not shown; the snapshot is complete | Phase 6 (version UX) |
| Streaming resume for answers | None: a disconnect cancels, and the user retries | A real need for flaky networks |

## Open Questions

None. (OQ-1, marker lists, was resolved on 2026-10-15: `[n, m]` lists are accepted and normalized to `[n][m]`; see Pipeline step 7.)
