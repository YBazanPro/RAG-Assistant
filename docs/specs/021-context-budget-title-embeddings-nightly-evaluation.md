# Spec 021: Context budget, titles in embeddings, nightly evaluation

**Status:** Approved (2026-10-20)
**Date:** 2026-10-20
**Roadmap:** hardening before spec 019 · **Builds on:** specs [003](003-continuous-integration.md), [007](007-background-ingestion-and-progress.md), [011](011-retrieval.md), [012](012-chat-conversations-and-grounded-answers.md), [017](017-diagnose-traces-admin-and-metrics.md), [018](018-evaluation-and-french-english.md) · **Decision records:** none (no new library or infrastructure)

**Order:** implemented before spec 019, so that the hybrid and rerank comparison starts from a prompt that always fits, embeddings that carry the document title, and a baseline measured every night.

## Problem

Three gaps found by a review of the RAG pipeline (2026-10-20):

1. **Nothing bounds the prompt.** Passage sizes are estimated as characters / 4, which undercounts French, and nothing sets Ollama's context window. Eight passages of up to 400 estimated tokens, plus the rules, the question and 1,000 output tokens, can pass a 4k window. Ollama then truncates the prompt silently, and what it cuts can be the system rules.
2. **Passages are embedded without their document.** The embedding input is the heading path plus the text. A plain-text file or a PDF without an outline embeds "the limit is 220 euros" without saying which policy it belongs to.
3. **Evaluation runs only by hand.** The harness needs real models, so it never runs in CI, and a regression in retrieval or answers shows only when someone runs it.

## Scope

### In

**Context budget (Chat module and API)**

- **One setting:** `Chat:Generation:ContextWindowTokens` (default 8192), the window the model is given and the budget the prompt must fit in. Validated at startup: greater than `MaxOutputTokens + 1024`.
- **The API sets Ollama's `num_ctx`** to that value on every chat call, in the same delegating client that sends `think` (renamed `OllamaRequestOptions`). Provider-specific, so it lives in the API. A hosted provider gets no `num_ctx`; its window is larger, and the budget still applies.
- **The budget is applied after retrieval, before `retrieval.completed`.** `PromptBudget` builds the real prompt (same rules, language and question) and estimates its tokens conservatively as characters / 3, plus 8 per message. While estimate + `MaxOutputTokens` is over the window and more than one passage is left, it drops the lowest-ranked passage. The best passage is always kept. The sources, the citation markers, the snapshots and the trace see only the passages that were sent, so numbering stays consistent.
- **Recorded:** the trace stores `TrimmedPassageCount` and `PromptTokenEstimate` (the estimate of the prompt that was sent). The admin request detail shows both next to the provider's input token count, so a wrong ratio is visible.
- No tokenizer library: the estimate errs on the large side, and the admin detail shows how far off it is.

**Document title in the embedding input (Documents module)**

- **Input:** `title > section > subsection` on the first line, a blank line, then the passage text. When the first heading equals the title (case-insensitive, trimmed), the title is not repeated. Without a section path, the first line is the title alone.
- **The title is read at ingestion time.** Editing a title later does not re-embed; a reprocess does. Queries are embedded as before.
- **Recorded per version:** `EmbeddingInputVersion` (`int`, null until indexed). `1` = heading path and text (before this spec), `2` = title, heading path and text. The migration sets `1` on every version that has an embedding model.
- **Old versions stay searchable.** Admin health (`GET /api/admin/documents/health`) gains `outdatedEmbeddings`: active documents whose active version has an older input version. The admin overview shows it with "reprocess to update". `GET /api/documents/{id}/versions` gains `embeddingInputOutdated` (null when the version is not indexed).
- **The harness keeps runs comparable:** `seed` reprocesses every sample document whose active version is outdated, and waits for it.

**Nightly evaluation (CI)**

- **Workflow** `.github/workflows/evaluation.yml`: nightly (`cron: 0 2 * * *`) and on `workflow_dispatch`, on `ubuntu-latest`, read-only permissions, no secrets, timeout 240 minutes.
- **Models:** `bge-m3` (embeddings, as always) and `qwen3:1.7b` as the chat model, pulled into the AppHost's Ollama volume before the AppHost starts, so the run never waits on a download.
- **Runs the real AppHost** (`--launch-profile https`, dev certificate trusted for .NET), waits for the API and Keycloak, then `dotnet run --project tools/RagAssistant.Evaluation -- run`. The judge pass is not run.
- **CPU deadlines:** the AppHost forwards every `ApiEnvironment:<NAME>` setting to the API as the environment variable `<NAME>`. The workflow uses it to raise `Chat__Generation__FirstTokenTimeout` (2 min), `Chat__Generation__TotalTimeout` (5 min) and `Chat__FollowUp__RewriteTimeout` (1 min).
- **Report only:** `report.md` goes to the run summary, and the report folder plus the AppHost log are uploaded as an artifact (30 days). The job fails only when the harness cannot run, seeding fails, or the report counts errors. No metric threshold fails it.

### Out

- A tokenizer library or exact token counts.
- Re-embedding on title edits; bulk reprocessing outside the harness.
- The judge pass in CI, metric thresholds, and comparisons with the previous night.
- The rewrite prompt's budget (its history window is already bounded by spec 014).

## Domain Model

| Module | Entity | Change |
|---|---|---|
| Documents | `DocumentVersion` | `EmbeddingInputVersion` (`int?`), set with the passages when the version is indexed. Migration `EmbeddingInputVersion`: `1` where `embedding_model` is not null. |
| Chat | `RequestTrace` | `TrimmedPassageCount` (`int`, default 0) and `PromptTokenEstimate` (`int?`, null when no prompt was built). Migration `PromptBudget`. |

## API Contract

| Method | Path | Change | Policy |
|---|---|---|---|
| GET | `/api/admin/documents/health` | adds `outdatedEmbeddings` | Maintainer |
| GET | `/api/documents/{id}/versions` | each item adds `embeddingInputOutdated` (`bool?`) | KnowledgeSeeker |
| GET | `/api/admin/chat/requests/{id}` | `generation` adds `trimmedPassageCount`, `promptTokenEstimate` | Maintainer |
| GET | `/api/admin/chat/configuration` | adds `contextWindowTokens` | Maintainer |

## Edge Cases & Failure Modes

| Case | Behaviour |
|---|---|
| The best passage alone exceeds the budget | It is sent anyway (a passage is at most `MaxTokens`, far below the window); the trace shows the estimate. |
| `NoEvidence` | No prompt, no budget: `PromptTokenEstimate` stays null. |
| A trimmed passage would have been cited | It cannot be: markers are numbered over the passages sent. |
| `ContextWindowTokens` ≤ `MaxOutputTokens + 1024` | The API refuses to start (options validation). |
| A document's title equals its first heading | The title appears once in the embedding input. |
| An outdated version is reprocessed | The new version has input version 2; the count drops. |
| Nightly: a model pull or the AppHost fails | The job fails before the harness, with the AppHost log uploaded. |

## Acceptance Criteria

### Context budget

- **AC-1** Given 8 passages whose prompt fits, then all 8 are sent and `TrimmedPassageCount = 0` (unit test).
- **AC-2** Given passages whose prompt does not fit, then the lowest-ranked are dropped until it fits, order is kept, and the number dropped is returned (unit test).
- **AC-3** Given a budget smaller than one passage, then the best passage is still sent.
- **AC-4** Given a tight `ContextWindowTokens` in an integration test, then `retrieval.completed` lists only the sent passages, a citation of the last sent passage is kept, and the trace and admin detail record the trimmed count and the estimate.
- **AC-5** Given Ollama as provider, then every chat request body carries `options.num_ctx` equal to `ContextWindowTokens`, and `think` as before (request captured from the Ollama client).
- **AC-6** Given `ContextWindowTokens = 1500` and `MaxOutputTokens = 1000`, then options validation fails.

### Embedding input

- **AC-7** Given a title and a heading path, then the input is `Title > A > B`, a blank line and the text; given a first heading equal to the title, then the title is not repeated; given no path, then the title alone (unit tests).
- **AC-8** Given a document ingested after this spec, then its version records `EmbeddingInputVersion = 2` and the fake embedder received inputs starting with the title.
- **AC-9** Given an active version with input version 1, then health reports `outdatedEmbeddings = 1` and its version item reports `embeddingInputOutdated = true`; after reprocessing, 0 and false.

### Nightly evaluation (verified on the first runs)

- **AC-10** Given a manual dispatch, then the workflow pulls both models, starts the AppHost, seeds, runs every golden case with `qwen3:1.7b`, writes the summary and uploads the report and the log.
- **AC-11** Given a report with errors, then the job fails; given only weak metrics, then it passes.

## Verification

Unit and integration tests cover AC-1 to AC-9. AC-10 and AC-11 are checked on the first manual dispatch after merge; the result goes here.

**2026-10-20, first dispatches:**
- **Run [39443083110](https://github.com/YBazanPro/RAG-Assistant/actions/runs/39443083110) failed before the harness:** the AppHost uses the Aspire CLI bundle (`AspireUseCliBundle`), so `dotnet run` starts `bin/Debug` whatever configuration is passed, and the Release-only build gave it nothing to start. The workflow now builds and runs in Debug.
- **Run [39445089883](https://github.com/YBazanPro/RAG-Assistant/actions/runs/39445089883) passed (AC-10):** both models were pulled, the AppHost started with the dev certificate trusted (Keycloak included), the 17 sample documents were seeded, and the 87 golden cases ran with `qwen3:1.7b` with 0 errors and 0 broken cases. The summary and the artifact are on the run page. The harness took about 10 minutes and the whole job 14, well under the estimate: `qwen3:1.7b` answers in about 4 s median on the 4-core runner.
- **AC-11:** the pass with weak metrics is shown by this run (behaviour 0.70, injection resisted 0.00). The failing path, a report with errors, has not happened yet.
- **Findings recorded in [spec 018's Calibration](018-evaluation-and-french-english.md#calibration):** the threshold sweep moved `ConfidentScore` to 0.55, and `qwen3:1.7b` fails the injection case.
