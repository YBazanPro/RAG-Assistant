# Spec 013: Chat UI

**Status:** Approved (2026-10-15)
**Date:** 2026-10-15
**Roadmap:** step 3.4 ([roadmap](../roadmap.md)) · **Builds on:** specs [005](005-web-shell.md) (shell, BFF), [008](008-browse-corpus-and-documents-ui.md) (document detail deep links, UI conventions), [012](012-chat-conversations-and-grounded-answers.md) (chat API and answer stream) · **Decision records:** [ADR 0001](../adr/0001-ui-ai-streaming-stack.md), [ADR 0007](../adr/0007-browser-smoke-tests-playwright.md)

## Problem

Spec 012 gives a complete chat API, but `/chat` is still a placeholder ("Chat is coming soon"). Knowledge seekers need Journey 1 ("Ask and verify") and Journey 2 ("Explore evidence") in the browser. They ask a question and watch the answer stream in. They need to tell a complete, partial, refused and failed answer apart at a glance. They open a citation's passage without losing the conversation, and they come back to earlier conversations. Citations are the product's main trust mechanism (`product.md`), so they must be readable, keyboard-reachable and always resolvable, even after the source changes.

## Scope

### In

- **Routes:** `/chat` (a new conversation) and `/chat/{conversationId:guid}` (an existing one), both with the `KnowledgeSeeker` policy.
- **Three panes:**
  - your conversations (left);
  - the thread with the input (centre);
  - `SourcePreview` (right), which opens when a citation is selected.
  - Below ~1024 px, the list collapses behind a button and the preview becomes an overlay drawer.
- **Components** in `Web.Client/Chat/`: `ConversationList`, `ConversationThread`, `QuestionInput`, `AnswerMessage` (with the stage line, partial warning, error, cancelled and pending notices), `AnswerText` (the safe Markdown subset), `CitationList`, `CitationItem`, `SourcePreview`, `RefusalNotice`.
- **Client services:**
  - `IChatApi` for the REST calls, returning `ApiResult<T>`;
  - `IAnswerStream` for the SSE POST;
  - `IThreadScroller` and `IClipboard`, small JS interop modules behind interfaces.
- Streaming with live `[n]` citation links, cancel, retry, copy reference, auto-scroll, a screen-reader status region, a draft kept across sign-in, and an empty state with a corpus summary and suggested questions.
- **Tests:**
  - bUnit component tests;
  - a BFF test showing that the answer POST streams through unbuffered;
  - Playwright smoke tests against an AppHost in **Demo** chat mode.
- The **Home** CTA ("Ask a question") already links to `/chat`; it is unchanged.

### Out

- Language and tag filters, and a `CorpusSelector`: every question searches the whole active corpus.
- Feedback buttons, follow-up behaviour and deleting conversations (spec 014).
- Renaming conversations.
- Showing retrieval scores or diagnostics to seekers (Phase 4).
- A separate `ConfidenceIndicator` badge. Confidence is expressed through the status only (see Domain Model).
- Live checks of a cited source's current state, and "this source has changed" (Phase 6, version UX).
- A question box on the Home page (Phase 6), and a copy-answer button.
- Polling or live tailing of an answer that is streaming in another tab.
- Automatic reconnects of the answer stream (answers are not resumable, spec 012).
- A Markdown library: the subset is parsed in-house (no ADR, no new dependency).

## Domain Model (client state)

No persistence beyond the API, except the input draft in `sessionStorage`.

### Page state

| State | Source |
|---|---|
| Current conversation | Route `{conversationId}`; `null` on `/chat` |
| Conversation list | `GET /api/chat/conversations`, pages of 20, appended by "Load more" |
| Thread | `GET /api/chat/conversations/{id}` (`ConversationDetail`) |
| Live answer | Built from the stream events; replaced by the server state at the end |
| Selected citation | `(assistantMessageId, marker)`; the preview shows that message's snapshot |
| Draft | `sessionStorage["ragassistant.chat.draft.<conversationId or 'new'>"]`, written on change and cleared on a successful `question.accepted`. Every access is wrapped in try/catch, because storage can be blocked |

### Creating a conversation

On `/chat`, nothing is created on page load.

1. The first **Send** calls `POST /api/chat/conversations`, then asks.
2. The URL is replaced with `/chat/{id}` (`ReplaceHistoryEntry`), so Back does not return to an empty `/chat`.
3. The draft moves from `new` to the new ID.

### Live answer (one at a time)

| Event | UI |
|---|---|
| Send pressed | The question appears immediately in the thread, and the input is cleared. The answer area shows "Searching the documents…" |
| `question.accepted` | The client keeps the message IDs and clears the draft |
| `retrieval.completed` | The stage line becomes "Found N passages. Writing the answer…" (`N` = number of sources). The sources are kept to resolve markers |
| `answer.delta` | Text is appended. Rendering is batched (see Non-Functional Requirements). Each `[n]` becomes a citation button as soon as it is complete |
| `answer.completed` | The final status view (below), then a re-fetch of the conversation, so the thread reflects the server state (status, text, snapshots) |
| `error` | The error view. Then the conversation is re-fetched |
| `heartbeat`, unknown events | Ignored |
| Stream ends with no terminal event, or a network error | "Connection lost." Then the conversation is re-fetched once and its server state is shown (usually `cancelled`, with Retry) |

### Answer views (`AnswerMessage`)

| Status | View |
|---|---|
| `pending` (opened from history) | Notice: "An answer is being written in another tab or window." with **Refresh**. The input is disabled |
| streaming | Stage line, then the text. `aria-busy="true"`. The input's Send button becomes **Cancel** |
| `complete` | The text with citation buttons, then `CitationList` (cited sources only, in marker order) |
| `partial` with citations | A warning `FluentMessageBar`: "Based on limited evidence. Check the sources.", then the text and `CitationList` |
| `partial` without citations | A warning: "This answer is not linked to any source." and the text. No `CitationList` |
| `refused` | `RefusalNotice`, an informational card that is not styled as an error. `no_evidence`: "The documents don't contain enough information to answer this." `model_declined`: "The passages found don't answer this question." Both suggest rephrasing or checking **Documents** (a link) |
| `failed`, retryable | An error `FluentMessageBar` with the message, and **Retry**, which calls the retry endpoint |
| `failed`, not retryable | An error bar with **Ask again**, which puts the question back into the input |
| `cancelled` | A neutral line, "Answer cancelled.", with **Retry** |

The user's question is always shown above its answer, whatever the status (`product.md`: "The user's question must never disappear").

### `AnswerText`: safe Markdown subset

- The text is parsed into a small block/inline tree and rendered as Razor elements. **`MarkupString` is never used.**
- **Blocks:** paragraphs (blank-line separated), line breaks inside a paragraph, `- `/`* ` unordered lists, and `1. ` ordered lists (one level).
- **Inline:** `**bold**`, `*italic*`, and `[n]` citation buttons, where `n` resolves against the message's sources. A marker that does not resolve is rendered as plain text.
- Everything else, including headings, links, code, tables and raw HTML, renders as literal text.

### Citations

- **`CitationItem`:**
  - shows the document title, the section path (`A › B`), the version (`version 3`) and the pages (`p. 4` or `pp. 4–5`) when known;
  - is a focusable button whose accessible name is "Source 1: Budget Exception Policy, Approval › Thresholds, page 4";
  - has a **Copy reference** action;
  - has a selected state.
  - The title and location render as plain text in the prerendered HTML too.
- **In-text `[n]` buttons** select the same citation as the list item.
- **`SourcePreview`** renders from the snapshot only, so it cannot fail to load:
  - title, section path, version number, pages;
  - "As cited on <date>" (the answer's `completedAt`);
  - the full passage text in `<pre>`.
  - **Open in document:** `/documents/{documentId}?version={n}&passage={passageId}`.
  - **Open page N**, when the passage has a page: the original file, as `PassageCard` does.
  - **Close** (button or Esc) returns focus to the citation that opened it.
  - Opening another citation replaces the content.
  - The conversation stays visible and scrollable next to it.
  - While streaming, the sources come from `retrieval.completed`, which carries no passage text. So during the stream the preview shows the title, section and pages with "The passage is shown when the answer completes." Once the answer completes, the snapshot fills it in.
- **Copy reference** copies `Budget Exception Policy — Approval › Thresholds (version 3, p. 4)` plus a newline and the absolute deep link, through `navigator.clipboard`. A short "Copied" confirmation follows. If the clipboard is blocked, the reference is shown in a selectable read-only field instead.

### `QuestionInput`

- A `FluentTextArea` that grows to about 6 lines.
- **Enter** sends, and **Shift+Enter** adds a new line. Enter does nothing while an IME composition is active.
- A counter appears past 900 characters. Above 1,000 characters (after trimming) Send is disabled, with a message. Whitespace-only text cannot be sent.
- While an answer streams, the text stays editable and Send becomes **Cancel**. Cancel disposes the stream, which aborts the request, and the server marks the answer `cancelled`.
- Focus returns to the input after each final status.

### Leaving during a stream

Switching conversation, starting a new one or leaving the page disposes the stream, which aborts the request. There is no confirm dialog. Coming back shows the answer as `cancelled` with **Retry**.

### Conversation list

- Pages of 20, newest activity first, with **Load more** at the end.
- Each item shows the title, or "New conversation" when the title is `null`, and the relative last activity (`time` element with an absolute `title`).
- The current conversation is highlighted (`aria-current="page"`).
- After an answer completes, the list re-fetches its first page, so the conversation moves to the top.
- Empty: "No conversations yet."
- A **New conversation** button goes to `/chat`.

### Empty state (no messages yet)

- "Ask about N active documents." N is the `totalCount` of `GET /api/documents` (the default excludes archived). With `N = 0`: "No documents are indexed yet." plus a link to Documents.
- 3–4 suggested questions from `Chat:SuggestedQuestions` in `Web.Client/wwwroot/appsettings.json`. Clicking one fills the input and focuses it; it does not send.

### Scrolling (`IThreadScroller`, JS module `wwwroot/js/chat.js`)

- While streaming, the thread follows the bottom, unless the user has scrolled up more than ~64 px.
- In that case a **Jump to latest** button appears; it scrolls back down and resumes following.
- Opening a conversation scrolls to its last message.

## API Contract (consumed)

Spec 012, unchanged:

- `POST /api/chat/conversations`
- `GET /api/chat/conversations?page&pageSize`
- `GET /api/chat/conversations/{id}`
- `POST …/messages` (SSE)
- `POST …/messages/{assistantMessageId}/retry` (SSE)
- `GET /api/documents?pageSize=1`, for the corpus count.

Client rules:

- POSTs carry `X-Requested-With: ragassistant` (the existing `RequestedWithHandler`).
- Streams use `SetBrowserResponseStreamingEnabled(true)`, `HttpCompletionOption.ResponseHeadersRead` and `SseParser` (sse-endpoints skill).
- A non-2xx response before the stream starts is returned as a typed failure (`ApiResult`), never as an exception into the component.

| Response | UI |
|---|---|
| 400 `validation.failed` | The field message under the input (should not happen, because the input checks the same rules) |
| 401 | `SignInRequired` inline. The draft is kept in `sessionStorage` and restored after sign-in |
| 404 (conversation) | `ErrorState` "Conversation not found", with a link to a new conversation |
| 409 `chat.answer_in_progress` | The pending notice ("being written in another tab or window", **Refresh**) |
| 409 `chat.conversation_full` | "This conversation is full." with **Start a new conversation**, which carries the draft over |
| 409 `chat.retry_not_allowed` | Re-fetch the conversation and show its state |
| 5xx, 502, 504, network (status 0) | An error bar with Retry. The question stays in the input when the conversation was never created |

## Authorization

- The pages need the `KnowledgeSeeker` policy (`[Authorize(Policy = PolicyNames.KnowledgeSeeker)]`). The nav link is already gated (spec 005).
- The UI only hides; the API decides. Another user's conversation ID gives a 404 view.
- No token reaches the browser. All calls go through the BFF cookie session.

## Edge Cases & Failure Modes

| Case | Behavior |
|---|---|
| Double Enter, or Send clicked twice | Ignored while a send is in flight, so only one ask is made |
| Cancel before `question.accepted` | The request is aborted. If the server never accepted the question, the conversation shows no new message and the question goes back into the input |
| Session expires mid-stream (BFF re-challenge) | The stream fails, then the re-fetch gets 401 and shows `SignInRequired`. The answer will be `cancelled` on the server and offer Retry after sign-in |
| A marker in the text that has no source | Plain text |
| HTML or script in the answer or passage text | Rendered as text (encoded); there is no `MarkupString` path |
| An archived or superseded cited document | The preview still shows the snapshot. "Open in document" may show the detail page's own archived or not-found state |
| A 200-message conversation | Renders; final messages do not re-render during a stream |
| `sessionStorage` blocked | No draft persistence, and no error |
| Clipboard blocked | Fallback read-only field |
| Prerender | The server renders only `PageHeader` and `LoadingState` and calls no API. Data loads in `OnAfterRenderAsync(firstRender)` |
| Narrow screen | The list sits behind a "Conversations" button. The preview is an overlay drawer with a focus trap; Esc closes it |

## Non-Functional Requirements

- **Rendering:**
  - deltas go into a buffer, and only the live `AnswerMessage` re-renders, at most every 50 ms (an injected `TimeProvider` timer) and once at the end;
  - final messages return `false` from `ShouldRender` once their status is final.
- **Accessibility:**
  - one visually hidden `aria-live="polite"` region announces stage changes only: "Searching the documents", "Writing the answer", "Answer complete, 2 sources", "Partial answer, 1 source", "Could not answer", "Answer cancelled", "Error: <message>";
  - streaming text is `aria-busy` until the answer is final;
  - every action is reachable by keyboard, focus stays visible, and status is never shown by colour alone (text plus glyph, as in the fluentui conventions).
- **Layout:** works at 375 px wide (phone) up to desktop, with no horizontal page scroll.
- **Visuals:** Fluent UI Blazor only, with Fluent tokens in `app.css` (fluentui-blazor-conventions skill).

## Integrations

- **Web.Client:**
  - `IChatApi`/`ChatApi`, `IAnswerStream`/`AnswerStream`, `IThreadScroller`, `IClipboard`, registered in `AddUiServices` with the lazily resolved `HttpClient`, so prerendering resolves them;
  - the JS module `wwwroot/js/chat.js` (scroll follow, clipboard, Enter/IME handling if Blazor's key events are not enough).
- **Web (BFF):** no code change. The YARP pass-through already covers POST streams; a test proves it.
- **AppHost / smoke tests:**
  - the smoke tests expect the AppHost in **Demo** chat mode (the switch from spec 012), documented in the `ui-smoke-tests` skill;
  - they import their own small Markdown document first;
  - the off-corpus refusal works in Demo mode because retrieval is real.
- **`product.md`:** the `ConfidenceIndicator` row is noted as folded into `AnswerMessage` (status only).

## Acceptance Criteria

bUnit tests (fake `IChatApi`, scriptable `IAnswerStream`, `ManualTimeProvider`), unless marked BFF or Smoke:

- **AC-1** Given `/chat`, when the page renders, then it shows the empty state with "Ask about N active documents" and the configured suggestions, and no conversation is created. Clicking a suggestion fills the input without sending.
- **AC-2** Given `/chat` and a typed question, when Enter is pressed, then the client creates a conversation, asks, and replaces the URL with `/chat/{id}`, and the question appears in the thread before any event arrives.
- **AC-3** Given Shift+Enter, then a new line is added and nothing is sent. Given whitespace only, or more than 1,000 characters, then Send is disabled.
- **AC-4** Given a scripted stream `question.accepted` → `retrieval.completed{3 sources}` → deltas `"Head [1" "] and Finance [3]."` → `answer.completed{complete, citations 1 and 3}`, then the stage line shows "Found 3 passages" before the text, `[1]` becomes a button only once complete, and the final view shows a `CitationList` with exactly sources 1 and 3.
- **AC-5** Given an answer with status `partial` (with citations), `partial` (none), `refused/no_evidence`, `refused/model_declined`, `failed` retryable, `failed` not retryable and `cancelled`, then each renders the view in the table above. The refusal is not an error-styled message bar, and only the retryable states show Retry.
- **AC-6** Given a retryable failure, when Retry is clicked, then the retry endpoint is called for that assistant message and its stream replaces the view.
- **AC-7** Given a streaming answer, when Cancel is clicked (or the user switches conversation), then the stream's token is cancelled and, after the re-fetch, the answer shows "Answer cancelled." with Retry.
- **AC-8** Given a stream that ends without a terminal event, then "Connection lost" is shown, the conversation is re-fetched once, and its server state is rendered.
- **AC-9** Given answer text containing `<script>alert(1)</script>`, `# Heading` and `[link](http://x)`, then all of it renders as literal text. `**bold**` and a `- ` list render as `strong` and `ul`/`li`.
- **AC-10** Given a selected citation, then `SourcePreview` shows the title, section path, version, pages, "As cited on…", the snapshot text in `<pre>` and the two links with the specified URLs. Close returns focus to the citation that opened it.
- **AC-11** Given Copy reference, then `IClipboard` receives the reference text plus the deep link. Given a clipboard that throws, then the fallback field shows the same text.
- **AC-12** Given a 401 on send, then `SignInRequired` is shown and the draft is in `sessionStorage`. Given a fresh render of the same conversation, then the draft is restored into the input.
- **AC-13** Given a 409 `chat.conversation_full`, then the "full" notice is shown, and **Start a new conversation** navigates to `/chat` with the draft carried over.
- **AC-14** Given a conversation whose last answer is `pending`, then the pending notice and Refresh are shown and the input is disabled.
- **AC-15** Given 25 conversations, then the list shows 20 and **Load more** appends 5. A `null` title shows "New conversation", and the current item has `aria-current="page"`.
- **AC-16** Given 20 deltas within 50 ms of fake time, then the live `AnswerMessage` renders at most twice: once by the timer and once at the end. Final messages do not re-render.
- **AC-17** Given the stage changes of a stream, then the live region text changes to the announced sentences in order, and the streaming text has `aria-busy="true"` until the answer is final.
- **AC-18** Given a prerender of `/chat/{id}` (`WebAppFactory`), then the HTML contains the shell and `LoadingState` only, and the stub API received no chat call.
- **AC-19 (BFF)** Given the stub API that sends `question.accepted`, then holds the rest until released, when the client POSTs through the BFF, then the first event is read before the release, so the POST stream is not buffered.

Smoke (Playwright, AppHost in Demo mode):

- **AC-20** Ask an on-corpus question by keyboard (type, Enter). The answer streams, a `[1]` citation appears, Tab reaches it, Enter opens the preview with the passage, Esc closes it and focus returns to the citation.
- **AC-21** An off-corpus question shows the refusal notice. It is not an error bar, and there is no Retry.
- **AC-22** Cancel during a stream shows "Answer cancelled." with Retry, and Retry produces a complete answer.
- **AC-23** At 375 px wide, the conversation list opens from its button and the preview opens as a drawer that Esc closes.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Question filters / `CorpusSelector` | None; the whole corpus is searched | A second corpus or audience, or user demand |
| Suggested questions source | Static list in Web.Client configuration | Phase 5.1 golden dataset / Phase 6 home page |
| "Source has changed" in the preview | Not shown; the snapshot is authoritative | Phase 6 (version UX) |
| Live view of an answer streaming in another tab | Notice + Refresh | A resumable answer stream |
| Richer Markdown (tables, code, links) | Rendered as literal text | Answers that need them, judged in Phase 5 |

## Open Questions

None.
