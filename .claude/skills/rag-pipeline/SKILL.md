---
name: rag-pipeline
description: How the Chat module answers a question in RagAssistant (spec 012) - the fixed pipeline, the prompt, the [n] citation format and its stream filter, the sentinel veto, answer statuses, snapshots, traces, and the fake chat clients for tests and demo mode. Use when changing the answer pipeline, the prompt, citations or answer statuses, when adding follow-ups (spec 014), or when writing tests that involve the chat model.
---

# The answer pipeline (Chat module)

Spec 012 is the contract; this skill says where things live and what not to break. Code: `src/Modules/Chat/RagAssistant.Modules.Chat/Answering/`.

## Shape

- **Pre-stream checks are a Mediator command** (`Features/AskQuestion/AcceptQuestion`, `Features/RetryAnswer/AcceptRetry`), so `ValidationBehavior` gives the usual 400 and the result maps to ProblemDetails (404, 409).
  - The handler locks the conversation row (`PendingAnswers.LockOwnedAsync`, `SELECT … FOR UPDATE`), fails stale pending answers, checks for a live pending answer and the message cap, then commits the user message and a `pending` assistant message.
  - A partial unique index (`ix_messages_one_pending_per_conversation`) backs "one answer in flight" in the database.
  - Only then does the endpoint return `TypedResults.ServerSentEvents(pipeline.RunAsync(accepted, ct))`.
- **`AnswerPipeline`:** a producer task runs the stages and writes `SseItem<string>` into a channel. The stream reads the channel and adds a `heartbeat` whenever nothing was sent for `HeartbeatInterval`. Its `finally` awaits the producer, so the request scope (and its `DbContext`) outlives the final write after a disconnect.
- **Stages:**
  1. `question.accepted`
  2. the follow-up rewrite (spec 014, below), then `retrieval.started {query}`
  3. `IDocumentSearch.SearchAsync`, then `retrieval.completed` with numbered sources
  4. `NoEvidence` → refuse without a model call
  5. otherwise build the prompt and stream it through the filter as `answer.delta` events
  6. one finalize transaction (message, snapshots, trace), then `answer.completed`
- **Failures after `question.accepted`** are `failed` plus an `error {code, message, retryable}` event:
  - `retrieval.embedding_unavailable`;
  - `chat.model_timeout` (`FirstTokenTimeout`, then `TotalTimeout`);
  - `chat.model_unavailable` (any exception from the model call);
  - `chat.internal`.
  - A client disconnect, whatever the cancelled call throws, gives `cancelled` with no text, and the trace is still written; the final write uses its own 5 s token.
- **No automatic model retries.** The user retries through `POST …/messages/{id}/retry`: last answer only, `failed`/`cancelled` only, same message, `Attempts + 1`, one trace row per attempt.
- **Stale answers:** `PendingAnswerSweeper` fails answers pending since before startup (`chat.interrupted`). The accept handlers fail pending answers older than `TotalTimeout + 1 min`.

## Prompt (`PromptBuilder`, the only place)

- The system rules:
  - English;
  - only facts from the passages;
  - cite every claim as `[n]`, separately (`[1][2]`, not `[1, 2]`);
  - concise but keeping conditions;
  - plain prose, lists allowed, no headings;
  - reply with exactly the sentinel when the passages do not answer.
- `LowConfidence` adds `LowConfidenceInstruction`.
- The user message is `Passages:` then, per chunk, `[n] Title — A › B (page 4)`, a newline, the text, a blank line; then `Question: …`. Lines end with `\n` on every platform. `DemoChatClient` parses this layout, so keep them in step.
- **Bump `PromptVersion` whenever the wording changes;** it is recorded in every trace.
- **No conversation history in the answer prompt.** Spec 014 sends history to the rewrite call only. The answer prompt gets the retrieval query as `Question:`, plus `The user wrote: …` when the rewrite changed it. `PromptVersion` 014.1.

## Follow-up rewrite (spec 014)

- **When:** the conversation has an earlier `complete` or `partial` answer (`FollowUpHistory.HasUsableAnswer`). Otherwise the question is used as is and no model call is made.
- **History:** `FollowUpHistory.Build`: the last `Chat:FollowUp:HistoryExchanges` (3) exchanges before the current question, oldest first. A complete or partial answer contributes its text with `[n]` markers removed, cut to `MaxAnswerChars` (1,000); a refused, failed or cancelled one contributes only its question. History reaches the rewrite and nothing else, so a wrong earlier answer cannot leak into a grounded one.
- **Call:** `chat.GetResponseAsync<RewriteResult>` (needs the `Microsoft.Extensions.AI` package, not only the Abstractions) with `PromptBuilder.BuildRewrite`, bounded by `RewriteTimeout` (10 s, below `Chat:Generation:TotalTimeout`; `FollowUpOptionsValidator` checks it). The last line of the prompt is `Current question: …`; the fakes parse it.
- **Fallback:** a timeout, a model error or an invalid result (empty, over 1,000 characters, unparsable) uses the original question and records `RewriteFallbackReason` (`timeout`, `model_error`, `invalid`) on the trace. Only the caller's cancellation stops the answer. `RewriteUsed` is true only when the result was used.
- **Stored:** the user message's `RetrievalQuery` (replaced on retry) and the trace's `RetrievalQuery`, `RewriteMs`, tokens and `RewritePromptVersion`. The history and the rewritten query are never logged at Information.
- **Fakes:** `ScriptedChatClient.ScriptRewrite(q)` / `ScriptRewriteRaw(text)` / `ThrowOnRewrite()` / `HoldRewrite()` + `WaitUntilRewriteHeldAsync()` + `RewriteCancellationObserved`, and `RewriteCalls` (separate from `Calls`). Without a script it returns the current question unchanged. `DemoChatClient` does the same for a structured request.

## Feedback and deletion (spec 014)

- `Features/Feedback`: `PUT/DELETE …/rating`, `POST …/reports`. `FeedbackTarget` decides who may give feedback on what (owner; complete, partial or refused answers; else `chat.feedback_not_allowed`). Reports are limited by `Chat:Feedback:MaxReportsPerAnswer` under the conversation lock. A retry clears the answer's rating and reports.
- `Features/DeleteConversation`: one `ExecuteDelete` of the conversation; the foreign keys cascade to messages, snapshots, ratings, reports and traces. It takes the same conversation lock as asking, so a second DELETE gets 404, and a live pending answer gives `chat.answer_in_progress`.

## Citations and the sentinel (`AnswerStreamFilter`)

- A pure state machine. The streamed text and the persisted text are identical; never post-process the saved text separately.
- **Sentinel:** text is held while the trimmed reply could still be the sentinel or a prefix of it. A reply starting with the sentinel followed by nothing or a non-word character is a veto: nothing is released, and the status is `refused/model_declined`. The sentinel later in the text is ordinary text.
- **Markers:** `[` is held until `]`, a character other than a digit, comma or space, or 32 characters.
  - `[n]` with `1 ≤ n ≤ k` passes;
  - out-of-range numbers are dropped and counted (`StrippedMarkerCount`);
  - `[n, m]` becomes `[n][m]`;
  - anything else (`[]`, `[abc]`, `[1,]`) is text.
- `CitedMarkers` (distinct) decides the snapshots.

## Statuses (`AnswerStatusRules`)

`NoEvidence` → refused/no_evidence; sentinel → refused/model_declined; `LowConfidence` → partial; `Sufficient` → complete with ≥ 1 valid citation, otherwise partial.

## Persistence

- `CitationSnapshot` copies the passage (ids, version, title, section, pages, score, **full text**). It is never updated, and nothing references the `documents` schema.
- `RequestTrace` holds decision data only: question text, filters, outcome and thresholds, each kept passage (`passageId`, `versionId`, `score`, `rank`, `marker`, `cited`) as jsonb, models, prompt version, tokens, sentinel, stripped count, status, timings, OTel trace id. **No passage text and no prompt (ADR 0004 D4).**

## Providers

- `src/RagAssistant.Api/Program.cs` registers `IChatClient` by `Chat:Provider`:
  - `Ollama`: AppHost resource `chat`, model const `ChatModel` in `AppHost.cs`;
  - `Demo`: `Api/Chat/DemoChatClient.cs`, extractive, cites `[1]`/`[2]`, never declines.
  - Both use `.UseOpenTelemetry(sourceName: "RagAssistant.Chat.Model").UseLogging()`.
- AppHost launch profile `https-demo` = Demo (no chat model pulled). The UI smoke tests use it.
- Modules never reference a provider package (`ModuleBoundaryTests`).

## Tests

- **Unit** (`tests/Modules/RagAssistant.Modules.Chat.Tests`): the filter, status rules, prompt, title.
- **Integration** (`tests/RagAssistant.Integration.Tests/Chat`):
  - use `ChatBed`, which has its own database, scripted vectors ("budget" → axis 0, `RetrievalBed.P(score)`), `Sufficient` ≥ 0.7 and `LowConfidence` ≥ 0.5;
  - `ScriptedChatClient` is the fake model (the rewrite calls are described above): `Script(tokens…)`, `HoldBeforeFirstToken()` + `Release()`, `ThrowAfter(n)` (one shot), `Calls` (prompts received), `CancellationObserved`.
  - The shared `RagAssistantAppFactory` registers one too, but its embedding model is unavailable, so use it only for pre-stream checks.
  - Read streams with `ChatBed.AskAsync`/`RetryAsync` (to the end) or `ChatBed.OpenAsync` + `LiveAnswer.ReadUntilAsync`/`DisconnectAsync`.
  - Give every user its own `sub`: `CreateClientAs(persona, subject: ChatRequests.NewUser())`.
- **Never a real model in `dotnet test`.** The calibration spike (spec 012, "Calibration") is a manual run.
