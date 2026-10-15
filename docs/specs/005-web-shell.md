# Spec 005: Web shell (Blazor Web App, BFF, Fluent UI)

**Status:** Approved (2026-10-13)
**Date:** 2026-10-13
**Roadmap:** step 0.6 ([roadmap](../roadmap.md)) · **Decision records:** [ADR 0001](../adr/0001-ui-ai-streaming-stack.md) (decision 5), [ADR 0002](../adr/0002-identity-and-authentication.md) (decision 4)

## Problem

The Host is a secured API with no user interface, and the three personas of `product.md` cannot yet sign in or see anything role-specific. Every UI slice from Phase 1 on (documents, chat, admin) needs the same foundation: an authenticated Blazor app, a backend-for-frontend (BFF) that keeps tokens out of the browser and forwards `/api/**` (REST and SSE) to the Host, an app layout with role-aware navigation, and a small set of shared components. Building it first means later UI steps only add pages. ADR 0001 and 0002 fixed the architecture; this spec fixes the details needed to implement and verify it, and records one product change: **the product is English only** (no French, no localization).

## Scope

### In

- `RagAssistant.Web` (Blazor Web App server, BFF) and `RagAssistant.Web.Client` (WebAssembly), wired into the AppHost with references to the Host and Keycloak.
- Fluent UI Blazor **v5.0.0** as the only component suite; pages use `InteractiveWebAssembly` with prerendering on.
- BFF: OIDC code + PKCE sign-in with the `ragassistant-web` client, cookie session, sign-out that also ends the Keycloak session, YARP forwarding of `/api/**` to the Host with the user's access token, no response buffering, CSRF protection for state-changing calls, authentication-state flow to WebAssembly.
- App layout: header (app name, navigation, theme toggle, display name, sign-out), light/dark/system theme, responsive navigation below ~768 px, skip link and landmarks.
- Pages: `/` (minimal Home), `/chat`, `/documents`, `/admin` (placeholders with `EmptyState`), `/access-denied`, a not-found page. No `/knowledge` route or link.
- Shared components: `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `ConfirmDialog`.
- A new test project `tests/RagAssistant.Web.Tests` (bUnit component tests and BFF integration tests under TUnit), included in CI.
- Realm file edits needed by the Web client (see Integrations).
- Documentation: README, CLAUDE.md, `product.md` and roadmap (English-only), notes on ADR 0001 and 0002.

### Out

- Any business page content, module, or production Host endpoint (Phases 1 to 3). No `/api/me`, no Import button.
- Localization of any kind: no resource files, no culture switching, no `LanguageSwitcher`.
- `/knowledge`, `CorpusSelector`, `Toast`, `Tag` components (added with the first slice that needs them).
- Access-token refresh (see Deferred Decisions), role badge in the header, public landing page.
- Antiforgery tokens for `/api` calls (replaced by the custom-header defense, below).
- Streaming-specific client code (`SetBrowserResponseStreamingEnabled`, `SseParser` helpers): added by the first SSE slice (1.4).
- Browser end-to-end tests (Playwright) and automated accessibility scans.
- Production deployment, HTTPS certificates beyond the Aspire dev defaults, rate limiting.
- The `fluentui-blazor-conventions` project skill (a follow-up after merge, per the roadmap).

## Domain Model

No persisted entities. State is the cookie session and transient UI state.

**Projects**

| Project | Role |
|---|---|
| `src/RagAssistant.Web` | Server host of the Blazor app: OIDC, cookie session, YARP, BFF endpoints, prerendering, static assets |
| `src/RagAssistant.Web.Client` | WebAssembly project: layout, routable pages, shared components, `HttpClient` to the Web's own origin |
| `tests/RagAssistant.Web.Tests` | TUnit project: bUnit tests for components/layout/nav, `WebApplicationFactory` tests for the BFF |

Layout, pages and shared components live in `Web.Client` so they run identically when prerendered and when interactive.

**Session**

- HTTP-only cookie (`ragassistant.session`), `SameSite=Lax`, `Secure` always except local plain-HTTP development. Tokens are stored server-side in the cookie ticket (`SaveTokens`), never sent to the browser as separate values and never exposed to WebAssembly.
- Session lifetime equals the access-token lifetime (1 hour, from the realm). After it, the principal is no longer accepted (see Edge Cases).
- Authentication state reaches WebAssembly through `AddAuthenticationStateSerialization` (server) and `AddAuthenticationStateDeserialization` (client), carrying `sub`, the display name and the flat `roles` claim.

**Roles in the UI.** UI decisions use the `roles` claim, the same names and policies as the Host (`PolicyNames` in Shared): `KnowledgeSeeker`, `ContentOwner`, `Maintainer`. The Web applies them to pages and navigation; the Host remains the authority for data.

**Navigation**

| Entry | Route | Visible to | Page policy |
|---|---|---|---|
| Home | `/` | any signed-in user | authenticated |
| Chat | `/chat` | `knowledge-seeker` and above | `KnowledgeSeeker` |
| Documents | `/documents` | `knowledge-seeker` and above | `KnowledgeSeeker` |
| Admin | `/admin` | `maintainer` | `Maintainer` |

Every test user holds `knowledge-seeker` through the composite roles, so seeker, owner and maintainer see Home, Chat, Documents; only the maintainer also sees Admin. A signed-in user with no known role sees Home only. The `/documents/{documentId}` detail route exists since [spec 008](008-browse-corpus-and-documents-ui.md).

**Theme.** Three modes: System (default, follows `prefers-color-scheme`), Light, Dark. The choice is persisted in the browser. Colors come from Fluent design tokens; the visual direction of `product.md` applies (sober, high-contrast light theme, a carefully designed dark theme, one primary and one accent color).

**Shared components** (in `Web.Client`, built on Fluent primitives)

| Component | Contract | States |
|---|---|---|
| `PageHeader` | `Title`, optional `Description`, optional `Actions` content | — |
| `EmptyState` | `Title`, optional `Description`, optional `Icon`, optional `Action` content | — |
| `ErrorState` | `Title`, `Message`, optional `Retry` callback and label, optional `Action` content; `role="alert"` | default, with retry |
| `LoadingState` | optional `Label` (default "Loading"); `role="status"` | — |
| `ConfirmDialog` | `Title`, `Message`, `ConfirmLabel`, `CancelLabel`, `Destructive` flag; `OnConfirm` / `OnCancel`; Escape cancels; focus is trapped and restored | closed, open |

**Home content (this step).** `PageHeader`, one sentence of value statement, and an "Ask a question" button to `/chat` (shown only if the user passes `KnowledgeSeeker`). Suggested questions, corpus summary and recent documents are Phase 6.

**Header display name.** The `name` claim, falling back to `preferred_username`. It is display only; identity is `sub`.

## API Contract

### BFF endpoints (served by the Web, not proxied)

| Verb | Route | Behavior |
|---|---|---|
| GET | `/bff/login?returnUrl=` | Anonymous. Starts the OIDC challenge. After sign-in redirects to `returnUrl` if it is a local path (starts with a single `/`, no scheme or host), otherwise to `/`. |
| POST | `/bff/logout` | Authenticated. Validates the built-in antiforgery token (the sign-out is a static form), clears the cookie and ends the Keycloak session (RP-initiated logout), then lands on `/` which redirects to sign-in. A GET returns 405. |

Both routes are outside `/api`, so the proxy route cannot shadow them. Blazor pages and BFF routes have no `/api` prefix; every Host endpoint, reached through the proxy, has one.

### Proxy (`/api/**`)

| Concern | Rule |
|---|---|
| Route | One catch-all route defined in code: `/api/{**catch-all}` → Host cluster (`https+http://host` through Aspire service discovery). Pass-through, **no path rewriting**: `GET /api/documents` reaches the Host as `GET /api/documents`. Every Host endpoint lives under `/api` (spec 002). |
| Authentication | The route requires a signed-in session. Without one: **401** `application/problem+json`, `code` = `auth.unauthenticated`, `traceId`. Never a redirect. |
| Token | Request transform sets `Authorization: Bearer <access token>` from the session. Any client-supplied `Authorization` header is discarded, and the `Cookie` header is never forwarded to the Host. |
| Roles | Not enforced in the Web proxy; the Host's policies decide (a seeker calling an owner endpoint gets the Host's 403). |
| CSRF | For `POST`, `PUT`, `PATCH`, `DELETE` the request must carry `X-Requested-With: ragassistant`. Missing: **403** ProblemDetails, `code` = `bff.csrf.header_missing`. `GET`/`HEAD` are not checked. The WebAssembly `HttpClient` adds the header through a delegating handler. |
| Streaming | Responses are streamed without buffering or compression for `text/event-stream`; headers pass through (`Cache-Control: no-store` stays). A client disconnect cancels the upstream request. |
| Gateway errors | Host unreachable, refused or timed out: **502**/**504** `application/problem+json`, `code` = `upstream.unavailable` / `upstream.timeout`, `traceId`. Host responses (including its 4xx/5xx ProblemDetails) pass through unchanged. |
| Operational endpoints | The Host's `/health`, `/alive`, `/openapi/v1.json` and `/scalar` are outside `/api`, so the proxy does not reach them. |

## Authorization

**Pages** (enforced by the Web with `[Authorize(Policy = …)]` and the fallback policy; the cookie scheme is the default scheme):

| Caller | `/` | `/chat`, `/documents` | `/admin` | unknown route |
|---|---|---|---|---|
| Anonymous | redirect to Keycloak sign-in (return URL kept) | same | same | same |
| Signed in, no known role | 200 | 403 Access denied | 403 Access denied | 404 Not found |
| KnowledgeSeeker / ContentOwner | 200 | 200 | 403 Access denied | 404 Not found |
| Maintainer | 200 | 200 | 200 | 404 Not found |

**BFF and proxy**

| Caller | `GET /bff/login` | `POST /bff/logout` | `/api/**` |
|---|---|---|---|
| Anonymous | challenge (302 to Keycloak) | 401/redirect to sign-in | 401 ProblemDetails |
| Signed in | redirect to return URL | signs out | forwarded with the user's token |

The fallback policy requires an authenticated user on every Web endpoint; `/bff/login` and the Development-only `/health` and `/alive` opt out with `AllowAnonymous`. Static assets and the WebAssembly bootstrap files are served to signed-in users only.

## Edge Cases & Failure Modes

- **Expired session.** When the stored access token has expired, the principal is rejected on the next request: page navigations are challenged (the Keycloak SSO session normally signs the user back in without a password prompt and returns to the same URL); `/api/**` calls get 401 ProblemDetails. A running SSE stream is not cut by expiry, because the token is only checked when a request starts.
- **WebAssembly after expiry.** A 401 from `/api` makes the calling component show an `ErrorState` ("Your session has expired") with a "Sign in" link to `/bff/login?returnUrl=<current path>`. No automatic redirect, so typed input is not lost silently.
- **Open redirect.** A `returnUrl` that is absolute, protocol-relative (`//host`), or contains a scheme is ignored; the user lands on `/`.
- **Role changes.** Roles are read at sign-in; a change in Keycloak applies at the next sign-in or after session expiry. Accepted.
- **Host down at startup or later.** The AppHost waits for the Host and Keycloak. After startup, an unreachable Host gives 502/504 ProblemDetails on `/api`; pages that do not call the API keep working.
- **Keycloak down.** Sign-in and new sessions fail with the standard OIDC error page; existing sessions keep working until their token expires.
- **Spoofed headers.** A client-sent `Authorization` or `X-Forwarded-*` header is not trusted by the Host; the proxy overwrites `Authorization`.
- **CSRF.** A cross-site form POST cannot carry the SameSite=Lax session cookie and cannot add the custom header; a same-site tool without the header gets 403 `bff.csrf.header_missing`.
- **Unauthorized deep link.** A seeker opening `/admin` gets HTTP 403 with the "Access denied" `ErrorState` (a link back to Home); the navigation never shows the link.
- **Unknown route.** HTTP 404 with a not-found `ErrorState`.
- **Prerender then interactive.** Pages load data once, not twice, and do not flash an unauthenticated state. (No data is loaded in this step; the pattern is fixed for Phase 1.)
- **Large or slow responses.** The proxy imposes no response size limit and a request timeout that does not cut SSE (a limit on connection setup only; value chosen in the plan).
- **Logging.** No tokens, cookies or `Authorization` headers are logged; `IdentityModelEventSource.ShowPII` stays off.

## Non-Functional Requirements

- First visit downloads the WebAssembly bundle; this cost is accepted (ADR 0001). Evergreen desktop browsers (Chrome, Edge, Firefox, Safari current versions) are supported.
- Desktop-first. The navigation collapses into a menu below ~768 px, and the shell stays usable on a phone; no dedicated mobile design.
- Accessibility baseline: semantic landmarks (`header`, `nav`, `main`), a skip-to-content link, a visible focus indicator, and keyboard-operable navigation, theme toggle and sign-out. Fluent UI v5 defaults are relied on for contrast. The full pass is Phase 6.
- No visible flash of the wrong theme on the first paint for a remembered choice (best effort, verified by hand).
- UI text is plain English literals. Page `<title>` follows "Page · RagAssistant".
- Local start-up: `dotnet run --project src/RagAssistant.AppHost` brings up Postgres, Keycloak, migrations, Host and Web; the dashboard links to the Web URL.
- CI (spec 003): the new tests run in the standard `dotnet test`, need no Keycloak and no extra Docker container, and add little time. The Web builds in Release with warnings as errors; `dotnet format --verify-no-changes` passes.
- Versions only in `Directory.Packages.props`; no inline `Version`.

## Integrations

- **AppHost:** `AddProject<RagAssistant_Web>("web")` with `WithReference(host)`, `WithReference(keycloak)`, `WaitFor(host)`, `WaitFor(keycloak)`, `WithExternalHttpEndpoints()`. The Host address is resolved through service discovery.
- **Keycloak (`ragassistant-web`):** `AddKeycloakOpenIdConnect("keycloak", realm: "ragassistant", …)` with authorization-code flow and PKCE S256, `SaveTokens`, client id `ragassistant-web` and the committed dev client secret supplied by configuration (never hard-coded in C#; same source as the realm file). `RoleClaimType = "roles"`.
- **Realm file edits (`ragassistant-realm.json`):** (1) `roles` is also emitted in the ID token (or the Web reads it from the access token; the plan picks one, the UI requirement is only that the `roles` claim reaches the principal); (2) `post.logout.redirect.uris` set so RP-initiated logout can return to the Web. The Keycloak smoke test of spec 004 keeps passing.
- **Packages** (all covered by ADR 0001/0002, so no new ADR; versions pinned in `Directory.Packages.props`): `Microsoft.FluentUI.AspNetCore.Components` 5.0.0 (plus its icons package if v5 splits it), `Yarp.ReverseProxy`, `Aspire.Keycloak.Authentication` (already pinned), the Blazor WebAssembly packages, and `bUnit` for tests. bUnit is confirmed to run under TUnit in this step; if it does not, work stops and an ADR records the alternative (ADR 0001).
- **Host:** receives the user's access token as a normal bearer token; its audience, issuer and policies are unchanged (spec 004).
- **Test infrastructure:** the Web test factory replaces OIDC sign-in with a test authentication handler that signs in a persona (same `Persona` idea as the Host tests), points the YARP cluster at an in-process stub Host, and uses a static OIDC configuration (no discovery call) to assert the challenge URL. The stub Host echoes the received `Authorization` and `Cookie` headers and streams a small SSE with delays. No Keycloak and no Postgres are started.
- **Docs:** README (how to run, Web URL, test users, sign-in), CLAUDE.md (Web project, `/api` pass-through proxy, custom-header CSRF), `product.md` and roadmap (English only, step 0.6 done), note in ADR 0001 (Fluent UI v5.0.0 chosen) and ADR 0002 (CSRF by custom header, no refresh, session lifetime equals token lifetime).

## Acceptance Criteria

**Proxy and BFF**

- **AC-1** Given a signed-in persona and a stub Host, when the client calls `GET /api/x/y?z=1`, then the stub receives `GET /api/x/y?z=1`, `Authorization: Bearer <the session's access token>`, and no `Cookie` header; and a client-supplied `Authorization` header is not forwarded.
- **AC-2** Given no session, when `/api/**` is called, then the response is 401 `application/problem+json` with `code` = `auth.unauthenticated` and a `traceId`, and the stub Host receives nothing.
- **AC-3** Given a signed-in session, when `POST`, `PUT`, `PATCH` or `DELETE` is sent to `/api/**` without `X-Requested-With: ragassistant`, then the response is 403 with `code` = `bff.csrf.header_missing`; with the header it is forwarded; `GET` needs no header.
- **AC-4** Given the stub Host streams SSE events with delays, when a signed-in client reads `/api/stream`, then the first event is received before the stream ends (not buffered) and the `text/event-stream` content type is preserved; and cancelling the client request cancels the stub's request.
- **AC-5** Given the stub Host is stopped or times out, when `/api/**` is called, then the response is 502/504 `application/problem+json` with `code` = `upstream.unavailable` / `upstream.timeout`; given the stub returns a 404 or 500 ProblemDetails, then it passes through unchanged.
- **AC-6** Given a session whose access token has expired (time-controlled test), when `/api/**` is called, then the response is 401; and when a page is requested, then the response is a challenge redirect.
- **AC-7** Given an anonymous request to `/` or `/chat?x=1`, when sent, then the response redirects to the Keycloak authorization endpoint with `response_type=code`, `client_id=ragassistant-web`, a PKCE `code_challenge` (S256), and, after sign-in, the original local URL is restored. A `returnUrl` that is absolute or protocol-relative on `/bff/login` is ignored and the user lands on `/`.
- **AC-8** Given a signed-in session, when `POST /bff/logout` is sent with a valid antiforgery token, then the session cookie is cleared and the user is redirected to the Keycloak end-session endpoint with an `id_token_hint`; a `GET /bff/logout` returns 405; a POST without the antiforgery token is rejected.
- **AC-9** Given the session cookie, when the response headers are inspected, then it is `HttpOnly`, `SameSite=Lax`, and `Secure` outside local plain-HTTP development; and no response body or HTML contains an access, refresh or ID token.

**Pages, layout and components**

- **AC-10** Given each persona signed in, when the layout renders, then navigation shows Home, Chat, Documents for all three and Admin only for the maintainer; given a user with no known role, then only Home is shown; there is never a Knowledge link.
- **AC-11** Given a seeker or owner, when they request `/admin` directly, then the response is HTTP 403 and shows the "Access denied" `ErrorState` with a link to Home; given a user with no known role requesting `/chat`, the same; given a maintainer, then `/admin` shows the `EmptyState` placeholder.
- **AC-12** Given an unknown route and a signed-in user, when requested, then the response is HTTP 404 with the not-found `ErrorState`.
- **AC-13** Given the layout, when rendered, then it has `header`, `nav` and `main` landmarks, a skip link that targets `main` and is the first focusable element, and the page `<title>` is "<Page> · RagAssistant"; nav links, theme toggle and sign-out are reachable and operable by keyboard.
- **AC-14** Given the theme toggle, when the user picks System, Light or Dark, then the matching theme is applied, the choice is remembered across reloads, and System follows the OS setting. The default for a new browser is System.
- **AC-15** Given a viewport narrower than 768 px, when the layout renders, then the navigation is collapsed behind a menu button that is keyboard-operable; at desktop width the links are visible inline.
- **AC-16** Given `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState` and `ConfirmDialog`, when rendered by bUnit, then each shows its contract content in every listed state; `ErrorState` has `role="alert"` and a working retry callback; `LoadingState` has `role="status"`; `ConfirmDialog` invokes `OnConfirm`/`OnCancel`, Escape cancels, and the destructive flag changes the confirm button's appearance.
- **AC-17** Given a signed-in user, when Home renders, then it shows the header, the value sentence and an "Ask a question" button linking to `/chat` (hidden when the user fails `KnowledgeSeeker`); the header shows the display name and a sign-out button.
- **AC-18** Given the WebAssembly `HttpClient`, when it sends a request, then its base address is the Web origin only and every unsafe-method request carries `X-Requested-With: ragassistant`.

**Build, run and docs**

- **AC-19** Given the repository after this step, when built and tested (including in CI), then it builds with warnings as errors in Release (WebAssembly included), `dotnet format --verify-no-changes` passes, no package has an inline `Version`, bUnit runs under TUnit, and AC-1 to AC-18 are covered by TUnit tests (AC-14's remembered theme and AC-15's collapse at the component/markup level).
- **AC-20** Given the AppHost run with Docker, when a person signs in as `seeker`, `owner` and `maintainer` in turn (signing out between them), then the Web is reachable from the dashboard, each lands on the Home page with the right navigation, the Admin page is accessible only to the maintainer (403 page for the others), sign-out returns to the Keycloak login screen with no password-less re-entry, and the theme toggle works in light and dark. This is a manual check recorded in the PR description.
- **AC-21** Given the repository after this step, then the README, CLAUDE.md, `product.md`, the roadmap (0.6 done, English only), and the notes on ADR 0001 and 0002 are updated as listed in Integrations, and no French or localization requirement remains in `product.md` or the roadmap.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Render mode granularity | Global `InteractiveWebAssembly` with prerendering for all routable pages | First-load weight or flicker becomes a problem; static-SSR shell pages with interactive islands |
| Theme persistence mechanism (cookie vs `localStorage`) | The plan chooses whichever avoids a flash on first paint; not user-visible otherwise | Visible flash on reload |
| Access-token refresh | None: re-challenge on expiry, relying on the Keycloak SSO session (supersedes the deferral in spec 004) | Sessions shorter than real use, or the SSO session lifetime becomes shorter than the token |
| Where the `roles` claim comes from (ID token vs access token) | The plan picks one; the realm file is edited if needed | — |
| Role badge in the header, `CorpusSelector`, `Toast`, `Tag`, `/knowledge` | Not built | The first slice that needs each one |
| Browser end-to-end tests with real Keycloak | Manual check (AC-20) | The shell regresses in ways the integration tests miss |
| `fluentui-blazor-conventions` project skill | Created with spec 008 (`.claude/skills/fluentui-blazor-conventions`) | Done |

## Open Questions

None.

## Implementation Notes

Choices made while implementing, none of which change the approved behavior (recorded 2026-10-13):

- **Roles claim** comes from the ID token (`id.token.claim: true` on the web client's roles mapper); the BFF does not decode the access token.
- **Theme persistence** is the toggle's own `localStorage` entry (`ragassistant.theme`), applied through Fluent's `IThemeService`. Fluent only stores the effective light/dark result, which cannot tell "System" from an explicit choice. A first paint may briefly show the system theme before the saved choice is applied.
- **Status pages:** 403 and 404 are produced by re-executing `/status/{code}` (the original status code is kept). These views are rendered **statically**, without WebAssembly, because a re-executed request carries no persisted authentication state and the client would otherwise see an anonymous user (found in the manual run). `/access-denied` and `/not-found` also exist as routable pages for client-side navigation.
- **Anonymous in the WebAssembly client:** the route-level fallback shows a "Sign in required" `ErrorState` with a link (no automatic redirect), as already specified for expired sessions. An automatic redirect looped when the state was missing.
- **Proxy timeouts:** connect 10 s; activity timeout 2 min (configurable through `Bff:ProxyActivityTimeout`, used by the tests).
- **Packages added** (all in the families approved by ADR 0001 and 0002): `Microsoft.Extensions.ServiceDiscovery.Yarp` (YARP destinations through Aspire service discovery) and `Microsoft.AspNetCore.Components.WebAssembly.Authentication` (`AddAuthenticationStateDeserialization`).
- **Shared constants:** `RagAssistant.Shared` targets ASP.NET Core and cannot run in WebAssembly, so `PolicyNames.cs` and `RoleNames.cs` are linked into `Web.Client` as source files.
- **Launch settings:** `RagAssistant.Web` has its own `launchSettings.json` (Development); without it Aspire starts the Web in Production, which breaks the OIDC metadata check.
- **Keycloak is HTTPS** (`https://localhost:8180`, Aspire development certificate). The README recipe was corrected (`curl -k`).
- **AC-19:** bUnit runs under TUnit (confirmed by a spike and by the component tests).
- **AC-20** was run by hand against the AppHost on 2026-10-13: seeker, owner and maintainer sign-in, role-based navigation, Admin 403 for seeker and owner, sign-out returning to the Keycloak login, theme toggle (remembered across reloads), narrow-viewport menu, and a real Keycloak token accepted by the Host through the proxy.
- **Review follow-ups (code review and security scan, 2026-10-13):** the Blazor WebAssembly packages are pinned to 10.0.13 (10.0.12 carries advisory GHSA-g27p-qf6j-c96c, remote code execution); every response carries `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin` and `Content-Security-Policy: frame-ancestors 'none'` (a full script policy is deferred); unhandled exceptions re-execute `/status/500`, a static generic view with no details; the theme toggle survives blocked browser storage.
