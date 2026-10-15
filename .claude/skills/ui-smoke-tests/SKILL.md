---
name: ui-smoke-tests
description: How to run, write and debug the Playwright browser smoke tests (tests/RagAssistant.Ui.Smoke) and how to drive the running app in a real browser. Use when a UI change touches dialogs, file inputs, focus, keyboard, multi-tab behaviour or anything bUnit's loose JavaScript mode cannot show, when closing a UI spec's manual verification, or when a UI bug only reproduces in a browser.
---

# Browser smoke tests (Playwright for .NET)

Decision and scope: [ADR 0007](../../../docs/adr/0007-browser-smoke-tests-playwright.md). The project `tests/RagAssistant.Ui.Smoke` is **outside `RagAssistant.slnx`** and never part of `dotnet test --solution`. It drives an already running app.

## Running

1. Start the app (needs Docker). The `https` launch profile is required: `http` fails Aspire's unsecured-transport check. Use `-c Release` so a running process does not lock Debug binaries.
   ```bash
   dotnet run -c Release --project src/RagAssistant.AppHost --launch-profile https
   ```
   Wait until `https://localhost:7053/health` returns 200 (Web, via Aspire) and `bge-m3` has been pulled, or ingestion stays queued and the tests time out.
2. First time on a machine: build once, then install Chromium with the script Playwright puts in the output folder:
   ```bash
   dotnet build tests/RagAssistant.Ui.Smoke -c Release
   powershell -NoProfile -File tests/RagAssistant.Ui.Smoke/bin/Release/net10.0/playwright.ps1 install chromium
   ```
3. Run: `dotnet test --project tests/RagAssistant.Ui.Smoke -c Release`. `HEADED=1` shows the browser; `RAGASSISTANT_WEB_URL` overrides `https://localhost:7053`.
4. Stop the app. Killing the `dotnet run` parent leaves `RagAssistant.AppHost.exe` holding ports (22004, 17268): kill that process too, or the next start fails with "address already in use".

## Writing a test

- Use `BrowserFixture.SignInAsync()` (fresh context signed in as `owner` / `owner-dev` from `Realms/ragassistant-realm.json`; pass another persona when a spec needs it). Create your own uniquely named documents (unique file content, otherwise `duplicate_content`); do not rely on or clean up shared data.
- Fluent UI Blazor elements are web components with **open shadow roots** (Playwright pierces them) and **no ARIA role on `fluent-button`**: use `page.Button("Save")` (text match, last visible) not `GetByRole(Button)`. Inputs: `#<id> input` (e.g. `#md-title input`, `#up-owner input`). Tabs do have `role=tab`.
- Dialogs render lazily and `fluent-dialog` is modal: use `WaitForDialogAsync(label, open)` and `IsDialogOpenAsync`; `FocusPathAsync` reads the focused element through shadow roots. Use Playwright's `Expect(...)` for anything that waits on the server (ingestion takes seconds; allow ~90 s).
- Real files: `Locator("input[type=file]").SetInputFilesAsync(path)`. Never set files by script from the page: that is how a scripted run hid the real upload bug earlier.
- Assert on visible behaviour, not Blazor internals. Keep it to a few flows per spec; logic belongs in bUnit and integration tests.

## Debugging in the Claude desktop app

The in-app browser pane stops compositing when hidden (screenshots time out, dialogs may not render). Prefer these Playwright scripts, or `HEADED=1`, for anything that needs rendering, focus or keyboard fidelity.
