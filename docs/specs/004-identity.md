# Spec 004: Identity (Keycloak, JWT bearer, policies)

> **Naming note (2026-10-15):** the backend project `RagAssistant.Host` has since been renamed `RagAssistant.Api` (Aspire resource `api`, configuration keys `services:api:*`, test stub `StubApi`). "The Host" in this document means that project.

**Status:** Approved (2026-10-13; amended 2026-10-13: anonymous callers get 401 on unmatched routes)
**Date:** 2026-10-13
**Roadmap:** step 0.5 ([roadmap](../roadmap.md)) · **Decision record:** [ADR 0002](../adr/0002-identity-and-authentication.md)

## Problem

Every Host endpoint is currently anonymous, and the three personas of `product.md` (Knowledge Seeker, Content Owner, System Maintainer) have no technical existence. Every business endpoint from Phase 1 on must be authenticated and role-checked, and every test must be able to act as a persona. Adding that later means rewriting every endpoint and every test, so the identity foundation is built first. ADR 0002 already decided the design; this spec fixes the details needed to implement and verify it. The Web BFF (OIDC sign-in, YARP) is step 0.6 and out of scope here.

## Scope

### In

- Keycloak container in the AppHost, configured only by the committed realm file `src/RagAssistant.AppHost/Realms/ragassistant-realm.json`.
- Realm content: clients `ragassistant-web`, `ragassistant-api`, `ragassistant-dev`; composite realm roles; three test users; flat `roles` claim; audience mapper; 1-hour access tokens.
- Host: JWT bearer authentication, the three role policies, policy name constants in Shared, a fallback policy requiring an authenticated user.
- ProblemDetails bodies for 401 and 403 (deferred from spec 002).
- OpenAPI bearer security scheme (deferred from spec 002).
- Test infrastructure: test signing key and JwtBearer reconfiguration in `RagAssistantAppFactory`, `Persona` + `CreateClientAs(...)` helper, per-policy test-only endpoints, negative-token tests.
- One Keycloak smoke test (Testcontainers, same realm file, same pinned image tag as the AppHost), run in `dotnet test` and CI.
- Migration of existing tests (spec 002 sample slice) to authenticated clients.
- Docs: README (test users, dev-token recipe), CLAUDE.md (auth/testing bullets), roadmap (0.5 done), spec 002/003 follow-up notes.

### Out

- `RagAssistant.Web`, OIDC sign-in, cookie session, YARP, token forwarding, refresh, antiforgery (step 0.6).
- Any production endpoint (no `/api/me`).
- An `ICurrentUser` abstraction (added by the first module that needs `sub`).
- Per-corpus or per-document access rules (ADR 0002, "Revisit when").
- Production identity provider configuration; the realm file is never deployed.
- Scalar OAuth integration (manual tokens are obtained by curl, see Integrations).
- Rate limiting, lockout, MFA, user self-registration, password reset flows.

## Domain Model

No persisted entities. Identity data lives in Keycloak, defined by the realm file.

**Realm `ragassistant`**

| Client | Type | Purpose |
|---|---|---|
| `ragassistant-web` | Confidential, authorization code + PKCE | Web BFF sign-in (used from step 0.6; defined now) |
| `ragassistant-api` | Audience only, no login | Audience value `ragassistant-api` |
| `ragassistant-dev` | Public, direct access grants (password grant) | Dev-only: curl token recipe and the smoke test |

`ragassistant-web` and `ragassistant-dev` both carry an audience mapper so access tokens contain `aud: ragassistant-api`.

**Roles** (realm roles, composite): `knowledge-seeker`; `content-owner` ⊃ `knowledge-seeker`; `maintainer` ⊃ `content-owner`.

**Users** (one realm role each; composites supply the lower ones; passwords are dev-only and listed in the README):

| Username | Password | Realm role | Effective `roles` claim |
|---|---|---|---|
| `seeker` | `seeker-dev` | `knowledge-seeker` | knowledge-seeker |
| `owner` | `owner-dev` | `content-owner` | content-owner, knowledge-seeker |
| `maintainer` | `maintainer-dev` | `maintainer` | maintainer, content-owner, knowledge-seeker |

**Token shape:** `iss` = `<keycloak>/realms/ragassistant`, `aud` contains `ragassistant-api`, `sub` = user id, flat `roles` array (protocol mapper on realm roles; composites expanded), access-token lifetime 1 hour.

**Policies** (names are constants in `RagAssistant.SharedKernel`, names only, no logic):

| Policy | Requires role |
|---|---|
| `KnowledgeSeeker` | `knowledge-seeker` |
| `ContentOwner` | `content-owner` |
| `Maintainer` | `maintainer` |

**Test personas** (`Persona` enum in the test project): `Anonymous`, `KnowledgeSeeker`, `ContentOwner`, `Maintainer`. A minted token for a persona carries the same effective `roles` as the matching Keycloak user (composites expanded), plus a `sub`.

## API Contract

No production endpoints are added. Conventions this step establishes:

| Concern | Rule |
|---|---|
| Authentication | Host validates JWT bearer tokens (`AddKeycloakJwtBearer`, realm `ragassistant`, audience `ragassistant-api`, `RoleClaimType = "roles"`). `RequireHttpsMetadata` is off in Development only. |
| Default | A **fallback authorization policy** requires an authenticated user for every endpoint. Endpoints opt out explicitly with `AllowAnonymous`. Groups add role policies with `.RequireAuthorization(PolicyNames.X)`. |
| Anonymous endpoints | `/health` and `/alive` (Development only, as today), `/openapi/v1.json` and `/scalar` (Development only). Nothing else. |
| 401 | `application/problem+json`, status 401, `code` = `auth.unauthenticated`, `traceId`; response carries `WWW-Authenticate: Bearer`. |
| 403 | `application/problem+json`, status 403, `code` = `auth.forbidden`, `traceId`; does not say which role is missing. |
| OpenAPI | The document declares an HTTP bearer (JWT) security scheme and marks protected operations with it. |
| Unmatched routes | The fallback policy also applies to requests that match no endpoint. Anonymous callers get 401 on unknown routes (and on `/health`, `/openapi/v1.json`, `/scalar` outside Development); authenticated callers still get 404/405 ProblemDetails as in spec 002. |

Test-only endpoints (integration test project, extend the spec 002 sample group, under `/api/_test`):

| Verb | Route | Policy |
|---|---|---|
| GET | `/api/_test/authenticated` | fallback only (any authenticated user) |
| GET | `/api/_test/seeker` | `KnowledgeSeeker` |
| GET | `/api/_test/owner` | `ContentOwner` |
| GET | `/api/_test/maintainer` | `Maintainer` |
| GET | `/api/_test/anonymous` | `AllowAnonymous` |

The existing spec 002 sample endpoints (`echo`, `not-found`, `conflict`, `throws`, `slow`) now require an authenticated user; their tests use an authenticated persona.

## Authorization

| Caller | `/api/_test/anonymous` | `authenticated` | `seeker` | `owner` | `maintainer` |
|---|---|---|---|---|---|
| No token | 200 | 401 | 401 | 401 | 401 |
| KnowledgeSeeker | 200 | 200 | 200 | 403 | 403 |
| ContentOwner | 200 | 200 | 200 | 200 | 403 |
| Maintainer | 200 | 200 | 200 | 200 | 200 |

Modules identify users by the `sub` claim, never by username or email (ADR 0002). No abstraction for reading it is added yet.

## Edge Cases & Failure Modes

- Expired token, wrong signature, wrong issuer, wrong audience: 401 with the `auth.unauthenticated` ProblemDetails.
- Token valid but `roles` claim missing or empty: authenticated, so the fallback passes; any role policy returns 403.
- Malformed `Authorization` header (not `Bearer`, garbage token): 401.
- Host started while Keycloak is unreachable: requests with tokens fail with 401 until the signing keys can be fetched; the Host does not crash. In the AppHost this window is avoided by `WaitFor(keycloak)`.
- Realm file edited: Keycloak runs without a data volume and a non-persistent container lifetime, so the realm is re-imported on every AppHost run; console edits are lost by design.
- Exceptions in the auth pipeline are not special-cased; unhandled ones follow spec 002 (500 ProblemDetails).
- Unauthenticated request to an unknown route: 401 (the fallback policy runs before routing fails). Unauthenticated wrong-method requests: see Deferred Decisions.

## Non-Functional Requirements

- Keycloak is dev-only; roughly 0.5–1 GB RAM and 15–30 s startup are accepted (ADR 0002).
- The Keycloak smoke test is a single test using one container; it runs in CI on `ubuntu-latest` with the runner's Docker.
- Ordinary integration tests start no Keycloak and add no measurable time.
- Committed dev secrets (user passwords, web client secret) exist only in the realm file and README; they are never deployed.
- No tokens, passwords or `Authorization` headers are logged.
- Build stays warnings-as-errors; `dotnet format --verify-no-changes` passes; versions live only in `Directory.Packages.props`.

## Integrations

- **AppHost:** `AddKeycloak("keycloak", port: 8180).WithRealmImport("./Realms")`; no data volume; default (session) container lifetime; admin password parameter generated into the AppHost user secrets; image `quay.io/keycloak/keycloak` tag `26.8` pinned in a constant with a "keep in sync with the test fixture" comment, like the Postgres tag. The Host gets `WithReference(keycloak)` and `WaitFor(keycloak)`.
- **Packages (approved by ADR 0002, no new ADR):** `Aspire.Hosting.Keycloak`, `Aspire.Keycloak.Authentication` (preview, pinned), and `Testcontainers.Keycloak` for the smoke test (same family as `Testcontainers.PostgreSql`). Versions in `Directory.Packages.props`.
- **Test factory:** `RagAssistantAppFactory` reconfigures JwtBearer via `PostConfigure<JwtBearerOptions>` to validate tokens signed with a test key, same issuer, audience and `roles` shape as Keycloak, so no metadata fetch happens.
- **Smoke test:** starts Keycloak via Testcontainers with the same realm file and the same pinned image tag as the AppHost, obtains a token for each test user through `ragassistant-dev`, and checks `aud`, `sub` and `roles`. Passwords are read from the realm file, not hard-coded.
- **Manual token recipe** (documented in README): with the AppHost running, a password-grant request to `http://localhost:8180/realms/ragassistant/protocol/openid-connect/token` with `client_id=ragassistant-dev` returns an access token to use as `Authorization: Bearer ...` on the Host.
- **Step 0.6:** relies on `ragassistant-web`, the audience mapper and the `roles` claim defined here.
- **CI (spec 003):** the smoke test is now part of the standard test run; spec 003's "Out of scope" note is updated.

## Acceptance Criteria

- **AC-1** Given a request without a token to any protected test endpoint, when sent, then the response is 401 `application/problem+json` with `code` = `auth.unauthenticated`, a `traceId` and a `WWW-Authenticate: Bearer` header.
- **AC-2** Given a valid token whose roles are insufficient, when it calls a role-protected endpoint, then the response is 403 `application/problem+json` with `code` = `auth.forbidden` and a `traceId`.
- **AC-3** Given each persona (KnowledgeSeeker, ContentOwner, Maintainer), when it calls each of the five test endpoints, then the status matches the Authorization table exactly.
- **AC-4** Given an expired token, a token signed with another key, a token with a wrong issuer, and a token with a wrong audience, when each calls a protected endpoint, then each gets 401.
- **AC-5** Given a malformed `Authorization` header, when sent, then the response is 401.
- **AC-6** Given a valid token with no `roles` claim, when it calls `/api/_test/authenticated` and `/api/_test/seeker`, then the first returns 200 and the second 403.
- **AC-7** Given the Host in Development, when `/health`, `/alive`, `/openapi/v1.json` and `/scalar` are requested without a token, then each responds successfully; given another environment, then anonymous requests to them get 401 (authenticated requests get 404 as in spec 002).
- **AC-8** Given an endpoint group that declares no authorization, when it is mapped, then the fallback policy makes it return 401 without a token (a new group cannot be accidentally anonymous).
- **AC-9** Given the OpenAPI document, when requested, then it declares an HTTP bearer security scheme and the protected test operations reference it.
- **AC-10** Given the existing spec 002 sample endpoints, when called by an authenticated persona, then all spec 002 behaviors still hold (their tests are migrated to authenticated clients); an unknown route under `/api` still returns 404 ProblemDetails.
- **AC-11** Given the Keycloak container started from the committed realm file and pinned image, when each test user obtains a token through `ragassistant-dev`, then the token has `aud` containing `ragassistant-api`, a non-empty `sub`, and exactly the effective `roles` listed in the Domain Model table (flat `roles` array).
- **AC-12** Given the AppHost, when run with Docker, then Keycloak (`quay.io/keycloak/keycloak:26.8`) starts on port 8180 with the realm imported, the Host starts after Keycloak is healthy, and the manual token recipe yields a token carrying the expected `iss`, `aud` and `roles`. (No production endpoint exists, so acceptance of a real Keycloak token by the Host is proven indirectly: the smoke test verifies the claims, the test-key tests verify the Host pipeline. Decided 2026-10-13.)
- **AC-13** Given the repository after this step, then the README lists the test users and the token recipe, CLAUDE.md mentions the `ragassistant-dev` client and the persona test helper, roadmap 0.5 is marked done, and spec 002/003 carry the follow-up notes.
- **AC-14** Given the full solution, when built and tested (including in CI), then it builds with warnings as errors, `dotnet format --verify-no-changes` passes, no package carries an inline `Version`, and AC-1 to AC-11 are covered by TUnit tests.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Unauthenticated request to an existing route with the wrong method (405 vs 401) | Accept whatever ASP.NET produces; not asserted by tests | A client or the BFF depends on it |
| `ICurrentUser` abstraction for `sub` | None; the first module that needs `sub` introduces it | Step 3.1 (Conversations) |
| Scalar OAuth login | Not configured; use the curl recipe | Manual testing becomes painful |
| Access-token refresh | Handled by the BFF in step 0.6 | Spec for step 0.6 |

## Open Questions

None.
