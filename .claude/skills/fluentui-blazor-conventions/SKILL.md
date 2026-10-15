---
name: fluentui-blazor-conventions
description: How the RagAssistant Blazor client (Web.Client) is written: Fluent UI Blazor 5 usage, app-owned domain components, URL-driven pages, prerender-safe loading and bUnit tests. Use when adding or changing a page or component in src/RagAssistant.Web.Client.
---

# Fluent UI Blazor conventions (Web.Client)

Spec 005 (shell) and spec 008 (Documents UI) are the references. Package: `Microsoft.FluentUI.AspNetCore.Components` 5.0.0, the only component suite.

## What comes from Fluent, what is ours

- Use Fluent components for everything they cover. In use: `FluentStack` (layout), `FluentCard`, `FluentText`/`FluentLabel`, `FluentButton`, `FluentLink`, `FluentBadge` (status, tags, steps), `FluentMessageBar` (errors, notices; with `ActionsTemplate` the text goes in `<ChildContent>`), `FluentTextInput`, `FluentSelect<TOption,TValue>` (`Items`, `OptionValue`, `OptionText`, `Multiple` + `SelectedItems`), `FluentDatePicker TValue="DateTime?"`, `FluentInputFile` (`Mode=Stream`, `OnInputFileChange`), `FluentTabs`/`FluentTab`, `FluentDataGrid` + `TemplateColumn`, `FluentPaginator` + `PaginationState`, `FluentDialog`, `FluentSpinner`, `FluentProviders` (once, in `MainLayout`).
- Inputs carry their own `Label`, `Required`, `Message`/`MessageCondition`/`MessageState="MessageState.Error"`; do not add separate `<label>` elements.
- `FluentDataGrid` renders a real `<table>` with `th aria-sort`. For server-side sorting, mark columns `Sortable` with a constant `SortBy` (`GridSort<T>.ByAscending(_ => 0)`, so the grid keeps the API's order), give each a `ColumnId`, translate `OnSortChanged` (`args.SortColumns[0].Column.ColumnId` and `.Ascending`) into the API `sort` value, and set `IsDefaultSortColumn`/`InitialSortDirection` from the current sort with `@key="Sort"` on the grid so the indicator follows the URL.
- A `FluentPaginator` outside a grid works with a standalone `PaginationState`: set `ItemsPerPage`, `SetTotalItemCountAsync` and `SetCurrentPageIndexAsync` from the API page, and ignore `CurrentPageIndexChanged` when the index equals the current one (it also fires for programmatic changes).
- Plain HTML is left only where Fluent has no counterpart: a `div` wrapper with `@ref` to focus a card (`FluentCard` exposes no element), the section-path `ol`, `pre` for passage text, `time` elements.
- Styling is layout glue in `src/RagAssistant.Web/wwwroot/app.css`, using Fluent tokens (`--colorNeutralForeground3`, `--colorBrandStroke1`, ...).
- Domain components are app-owned and live next to their feature (`Web.Client/Documents/`): `DocumentTable`, `DocumentFilterBar`, `DocumentStatusBadge`, `DocumentUpload`, `IngestionStatus`, `DocumentMetadataPanel`, `PassageList`, `PassageCard`, `VersionHistory`. Shared states live in `Components/`: `PageHeader`, `LoadingState`, `EmptyState`, `ErrorState`, `SignInRequired`, `ConfirmDialog`.
- Status is a `FluentBadge` with text plus a glyph (`aria-hidden`), never colour alone; the glyphs are plain text, so no icon package types are needed.

## Razor gotchas that bit us

- A **string-typed** component parameter written without `@` is a literal: `Sort="_query.Sort"` passes the text `_query.Sort`. Write `Sort="@_query.Sort"`. Non-string parameters (`Query="_query"`, `Id="DocumentId"`) are expressions.
- `@bind` on `<input type="date">` infers `DateTime`; for string state use `value="@x" @onchange="@(e => x = e.Value?.ToString())"`.
- Fluent buttons render as `<fluent-button>` (tests: `FindAll("fluent-button")`); attributes such as `aria-*` and `class` pass straight through.
- Name clashes: a page class named like a model type breaks the model (`DocumentDetail` page vs. the response record, now `DocumentInfo`). Keep models in `RagAssistant.Web.Client.Documents` and pages in `Pages`, with distinct names.
- Models must be `public`: `Web.Client` has no `InternalsVisibleTo` for the tests.

## Pages

- State lives in the URL: `[SupplyParameterFromQuery(Name = "...")]` string properties, parsed by `DocumentQuery.FromRaw`, which drops invalid values. Filter, search and sort changes reset `page`; typing replaces the history entry (`NavigationOptions.ReplaceHistoryEntry`).
- **Prerender-safe loading:** the server prerender renders only the shell (`PageHeader` + `LoadingState`) and calls no API. Fetch in `OnAfterRenderAsync(firstRender)`; react to later URL changes in `OnParametersSetAsync` once `_started` is true. Services a page injects must resolve on the server too: `IDocumentsApi` and `IIngestionEventSource` are registered in `AddUiServices` with a lazily resolved `HttpClient` (it only exists in the browser).
- All calls go through `IDocumentsApi` (`ApiResult<T>`, never exceptions for HTTP failures; status 0 = network). 401 shows `SignInRequired`, 404 a not-found `ErrorState`, everything else an `ErrorState` with retry.
- Role checks use `IAuthorizationService` with the policy names (composite roles included) and only hide controls; the Host decides.
- Timers and delays go through the injected `TimeProvider` (`Clock.CreateTimer`, `Task.Delay(ts, Clock, ct)`); components cancel and dispose them in `Dispose`.
- Text from the Host (titles, passage text) is always rendered encoded; never `MarkupString`. Passage text is shown in `<pre>`.

## Tests (`tests/RagAssistant.Web.Tests`)

- Fluent inputs raise value events from JavaScript, which bUnit does not run: drive them through the component instance (`FindComponents<FluentTextInput>().First(c => c.Instance.Label == "Owner").Instance.ValueChanged.InvokeAsync(...)`; helpers in `Documents/FluentTestHelpers.cs`), not with `.Input()`/`.Change()` on markup. `FluentInputFile` still contains a real `InputFile`, so `FindComponent<InputFile>().UploadFiles(...)` works.
- bUnit under TUnit: `new BunitContext()`, `JSInterop.Mode = Loose`, `AddFluentUIComponents()`, `AddAuthorization().SetAuthorized(..).SetPolicies(..)`, then register fakes (`FakeDocumentsApi`, `FakeEventSource`, `ManualTimeProvider` in `Documents/TestDoubles.cs`). `[SupplyParameterFromQuery]` works after `NavigationManager.NavigateTo(url)` before rendering.
- `WaitForState`/`WaitForAssertion` re-check only when the component renders. Timers, stream connections and clock-driven work do not render, so poll for those (`PollAsync`). TUnit assertions cannot be awaited inside `WaitFor*`: use plain boolean predicates there.
- Use the fakes' gates (`ListGate`, `ImportGate`) to observe loading states; never block a dispatcher thread with `.Wait()`.
- Prerender and BFF behaviour is tested with `WebAppFactory` against the stub Host (`DocumentsBffTests`); the WebAssembly runtime itself is not started in tests.
