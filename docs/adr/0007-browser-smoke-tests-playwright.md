# ADR 0007: Browser Smoke Tests (Playwright for .NET)

**Status:** Accepted
**Date:** 2026-10-15
**Relates to:** [ADR 0001](0001-ui-ai-streaming-stack.md) (testing), [ADR 0005](0005-api-kernel-libraries.md) (dependency rule), [spec 009](../specs/009-document-lifecycle.md)

## Context

bUnit runs components without a browser. The manual walkthrough of spec 009 found two defects it cannot see: the upload dialogs never sent their request (a multipart form disposed before the browser read it) and Escape did not close the dialogs (the web component swallows the keydown before Blazor sees it). Both only show with a real browser, a real file stream and a real web component. `CLAUDE.md` requires an ADR for every new library.

## Decision

Add `Microsoft.Playwright` (Apache 2.0, first-party Microsoft) to one new test project, `tests/RagAssistant.Ui.Smoke`, written with TUnit like the other test projects.

- **Not part of `dotnet test --solution`.** The project is not in `RagAssistant.slnx`, like the answer-quality evaluation harness. It drives an already running AppHost (Keycloak, API, Web, Ollama), so it is slow and needs Docker and the embedding model; it is run on demand and before closing a UI-heavy spec, not on every change.
- **Smoke, not regression coverage.** A few end-to-end flows per UI spec, in a real browser, covering what bUnit cannot: real file input, dialogs, focus, keyboard, multi-tab behaviour. Logic stays in the bUnit and integration tests.
- **Dev personas only**, from the committed realm file (`owner` / `owner-dev`). Tests create their own uniquely named documents and leave them behind; the dev database is not a test fixture.
- The Chromium build is installed once per machine with the `playwright.ps1` script in the build output (see the `ui-smoke-tests` skill).

## Consequences

- One new package in `Directory.Packages.props` (`Microsoft.Playwright`) and a ~110 MB browser download per machine.
- The suite is not run by CI; if CI later needs it, a job must start the AppHost first.
- Because the project is outside the solution, `dotnet build RagAssistant.slnx` does not compile it: build it explicitly (`dotnet build tests/RagAssistant.Ui.Smoke`).
- Rejected: Playwright in the bUnit project (no real browser or server), Selenium (heavier driver story, weaker auto-waiting), and keeping only manual walkthroughs (they found the bugs but cannot be repeated).
