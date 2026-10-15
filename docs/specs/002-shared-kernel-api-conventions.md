# Spec 002: Shared kernel and API conventions

**Status:** Approved (2026-10-13)
**Date:** 2026-10-13
**Roadmap:** step 0.3 ([roadmap](../roadmap.md))

## Problem

Every upcoming slice (Documents, Chat, identity, the Web BFF) needs the same plumbing: a way to return success/failure, dispatch commands, validate requests, map endpoints, and report errors to clients. Today the Host has a placeholder `MapGet("/")` and the Shared project only contains persistence helpers. Without agreed conventions each slice would invent its own error shape and test setup, and the Web BFF and UI would have nothing stable to key off. The project is English-only (UI, messages, errors): there is no localization, so `Error.Message` is the user-facing English text and `code` exists for programmatic handling and tests.

This step builds that backbone once, small, and proves it with a test-only sample slice.

## Scope

### In

- `Result` / `Result<T>` and `Error` in `RagAssistant.Shared/Common`.
- Mapping from `Result` to `IResult` built on `TypedResults` and ProblemDetails (RFC 9457).
- Per-module Mediator wiring (source-generated Mediator) and a shared validation pipeline behavior using FluentValidation.
- `IEndpointGroup` and `app.MapEndpoints()` auto-discovery by reflection over registered assemblies, mounted under `/api`.
- ProblemDetails for all error responses: validation, handler failures, unhandled exceptions (`IExceptionHandler`), and framework errors (404, 405, 415, malformed body, oversized body).
- Client-abort handling (swallowed quietly).
- Built-in OpenAPI document and Scalar UI, Development only.
- `TimeProvider.System` registration.
- Removal of the placeholder `MapGet("/")` from the Host.
- A test-only sample endpoint group and handler in the integration test project, registered by `RagAssistantAppFactory`.
- ADR 0005 recording the new libraries and the Mediator topology.
- Update CLAUDE.md and roadmap 0.3 to drop Serilog (see Integrations).

### Out

- Authentication and authorization (step 0.5). The sample group is anonymous.
- API versioning (Asp.Versioning), CORS (the BFF is same-origin), rate limiting, response compression, idempotency keys. Revisit each only when a concrete need appears.
- Request-logging middleware, log enrichers, any logging library beyond built-in `ILogger` + the existing OpenTelemetry export.
- Pagination conventions (decided in the first slice that lists data: [spec 008](008-browse-corpus-and-documents-ui.md)).
- Any module, DbContext or real endpoint.
- A `Shared.Tests` unit-test project.
- Pipeline behaviors other than validation (no request logging, no exception-to-Result conversion).

## Domain Model

Code-level types, not persisted:

- `Error(string Code, string Message, ErrorType Type)` — a record. `Code` is a stable machine-readable string such as `documents.not_found`.
- `ErrorType` — `Validation`, `NotFound`, `Conflict`, `Forbidden`, `Failure`.
- `Result` / `Result<T>` — success, or failure carrying one `Error`. Validation failures additionally carry the field errors (field name to messages).
- `IEndpointGroup` — contract for a feature's endpoints; receives the `/api` route group and maps its routes.

Placement: `Result`, `Error`, `ErrorType`, `IEndpointGroup`, `MapEndpoints()`, the Result-to-HTTP mapping and the validation behavior live in `RagAssistant.Shared` (modules reference only Shared). Exception handler, ProblemDetails setup, OpenAPI/Scalar and the Host-level `MapEndpoints()` call live in the Host. Shared contains no business logic.

## API Contract

Conventions (no real endpoints in this step):

| Concern | Rule |
|---|---|
| Route prefix | All endpoint groups are mapped under `/api`. YARP forwards `/api/**` untouched. `/health` and `/alive` stay outside it. |
| Discovery | `MapEndpoints()` scans every assembly registered for it (the Host assembly plus each module assembly registered by `AddXxxModule`) for non-abstract `IEndpointGroup` implementations. |
| Return types | Endpoints return `IResult` built with `TypedResults`. Handlers return `Result<T>`; endpoints convert with `result.ToHttpResult(onSuccess)`. |
| Error body | Every error response is `application/problem+json`. |
| Error identification | ProblemDetails carries a `code` extension (from `Error.Code`) and a `traceId` extension. `type` keeps the default RFC link. |

`ErrorType` to HTTP status (proposed — confirm in review): `Validation` 400, `NotFound` 404, `Conflict` 409, `Forbidden` 403, `Failure` 500.

Validation failures use `HttpValidationProblemDetails`: status 400, `errors` dictionary with **camelCase** field keys mapped to message arrays, plus `code` = `validation.failed` and `traceId`.

Other error responses:

| Case | Status | Body |
|---|---|---|
| Unknown route | 404 | ProblemDetails |
| Wrong HTTP method | 405 | ProblemDetails |
| Unsupported media type | 415 | ProblemDetails |
| Malformed JSON body | 400 | ProblemDetails (`code` = `request.malformed`) |
| Oversized body | 413 | ProblemDetails |
| Unhandled exception | 500 | ProblemDetails, generic text, `traceId`; exception detail only in Development |
| Client aborted | none | Nothing written; logged at Debug only |

Test-only sample (integration test project, not shipped in the Host):

| Verb | Route | Behavior |
|---|---|---|
| POST | `/api/_test/echo` | Validated command; returns the echoed value, or 400 validation problem |
| GET | `/api/_test/not-found` | Handler returns `Error(NotFound)` |
| GET | `/api/_test/conflict` | Handler returns `Error(Conflict)` |
| GET | `/api/_test/throws` | Handler throws |
| GET | `/api/_test/slow` | Awaits cancellation (for the abort test) |

## Authorization

None in this step. All endpoints, including the test-only group, are anonymous. Step 0.5 adds JWT bearer and the policies, and will update the sample group.

## Edge Cases & Failure Modes

- Validation runs before the handler; a failing validator means the handler is never called.
- A request type with no validator passes straight through the behavior.
- Several validation failures on one field produce one array under that camelCase key.
- Unhandled exception: logged once with the exception at Error level; the client sees a generic 500. In Development the exception text is added to `detail`.
- Client abort: `OperationCanceledException` while `RequestAborted` is triggered is swallowed; no error log, no 500 metric.
- (Implementation note: ASP.NET's `ExceptionHandlerMiddleware` already drops client aborts with a Debug entry, "The request was aborted by the client.", so our exception handler never sees them and has no abort branch.)
- An `OperationCanceledException` not caused by client abort is treated as an unhandled exception.
- Two `IEndpointGroup` classes registering the same route fail at startup (ASP.NET behavior); not specially handled.
- `MapEndpoints()` finds no groups: no error (a module-less Host is valid).
- Error `Code` values are not validated for uniqueness in this step.

## Non-Functional Requirements

- Build stays warnings-as-errors; `dotnet format --verify-no-changes` passes.
- Endpoint discovery runs once at startup; no per-request reflection.
- The behavior and mapping add no measurable latency; no performance budget set for this step.
- Package versions are added centrally in `Directory.Packages.props`.
- Exception details never reach clients outside Development.
- No full request bodies are logged.

## Integrations

- **Logging:** Serilog is dropped. The Host uses built-in `ILogger` with the OpenTelemetry log export already configured in ServiceDefaults (console and Aspire dashboard). CLAUDE.md (Tech Stack line, "Serilog + OpenTelemetry") and roadmap 0.3 / Kit skills are updated accordingly.
- **New packages (ADR 0005):** Mediator (source-generated, MIT; `Mediator.Abstractions` + `Mediator.SourceGenerator`), FluentValidation, FluentValidation.DependencyInjectionExtensions, Scalar.AspNetCore, Microsoft.AspNetCore.OpenApi. All versions in `Directory.Packages.props`.
- **Mediator topology:** one Mediator per module, registered in that module's `AddXxxModule` with its own generated namespace; the shared validation behavior is registered into each. The test project gets its own Mediator for the sample slice. *Amended 2026-10-13 after the spike (Mediator 3.0.2):* two generated mediators collide on `IMediator` / `ISender` (first registration wins), so a module injects its **own concrete generated `Mediator` class** and never `IMediator` / `ISender`. Options are set through the `AddMediator(o => ...)` lambda (`o.Namespace`, `o.PipelineBehaviors`), not an assembly attribute.
- **OpenAPI / Scalar:** document at `/openapi/v1.json`, Scalar UI at `/scalar`, Development only. Bearer scheme is added in step 0.5.
- **BFF:** the `/api` prefix and ProblemDetails `code` are the contract step 0.6 builds on.
- **Result library:** `Result` / `Result<T>` is hand-rolled; ErrorOr was considered and rejected (extra dependency, multi-error accumulation not needed, and owning the pattern suits the demo).
- **ADR 0005 scope:** the libraries above and the per-module Mediator wiring. The error contract itself lives in this spec.

## Acceptance Criteria

- **AC-1** Given a valid request to the sample echo endpoint, when it is posted, then the response is 200 with the echoed value.
- **AC-2** Given an invalid request body, when posted to the sample echo endpoint, then the response is 400 `application/problem+json` with `errors` keyed by camelCase field names, `code` = `validation.failed` and a `traceId`, and the handler was not invoked.
- **AC-3** Given a handler returning `Error(NotFound)`, when called, then the response is 404 ProblemDetails with the error's `code`.
- **AC-4** Given a handler returning `Error(Conflict)`, when called, then the response is 409 ProblemDetails with the error's `code`.
- **AC-5** Given a handler that throws, when called in a non-Development environment, then the response is 500 ProblemDetails with a generic message, a `traceId`, and no exception text; the exception is logged once at Error.
- **AC-6** Given the same throwing handler in Development, when called, then `detail` includes the exception text.
- **AC-7** Given an unknown route under `/api`, when requested, then the response is 404 `application/problem+json`.
- **AC-8** Given a known route with the wrong method, when requested, then the response is 405 ProblemDetails.
- **AC-9** Given a POST with a malformed JSON body, when sent, then the response is 400 ProblemDetails with `code` = `request.malformed`.
- **AC-10** Given a POST with an unsupported content type, when sent, then the response is 415 ProblemDetails.
- **AC-11** Given a request that the client aborts mid-flight, when the cancellation reaches the handler, then no error is logged, no error body is written, and the server stays healthy.
- **AC-12** Given the Host at startup, when `MapEndpoints()` runs, then every `IEndpointGroup` in the registered assemblies is mapped under `/api`, and `/health` and `/alive` still respond.
- **AC-13** Given the Host, when requested at `/`, then no placeholder response is served (404 ProblemDetails).
- **AC-14** Given the Development environment, when `/openapi/v1.json` and `/scalar` are requested, then both respond; given any other environment, then both return 404.
- **AC-15** Given `TimeProvider` is requested from the container, then the system `TimeProvider` is returned.
- **AC-16** Given the full solution, when built and tested, then it builds with warnings as errors, `dotnet format --verify-no-changes` passes, and every AC above is covered by a TUnit integration test using the test-only sample group.
- **AC-17** Given the repository after this step, then ADR 0005 exists, CLAUDE.md and roadmap 0.3 no longer mention Serilog, and no package carries an inline `Version`.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| ~~Does per-module Mediator work?~~ **Resolved 2026-10-13:** yes, with the rule that modules inject their concrete generated `Mediator`, not `IMediator`/`ISender`. A single Host-level Mediator was rejected (sees all modules, needs public handlers). | n/a | n/a |
| Pagination convention | Decided in [spec 008](008-browse-corpus-and-documents-ui.md): offset paging, `page`/`pageSize`, `Page<T>` envelope in `RagAssistant.Shared/Common` | Done |
| Error `Code` uniqueness / catalog | No enforcement; codes are `<area>.<reason>` by convention | A duplicate code causes confusion |
| Auth challenge/forbidden ProblemDetails bodies | Added in [spec 004](004-identity.md) (`auth.unauthenticated`, `auth.forbidden`) | Done |

## Open Questions

None.
