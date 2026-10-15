# ADR 0005: API Kernel Libraries and Mediator Topology

> **Naming note (2026-10-15):** the backend project `RagAssistant.Host` has since been renamed `RagAssistant.Api` (Aspire resource `api`, configuration keys `services:api:*`, test stub `StubApi`). "The Host" in this document means that project.

**Status:** Accepted
**Date:** 2026-10-13
**Relates to:** [ADR 0001](0001-ui-ai-streaming-stack.md), [spec 002](../specs/002-shared-kernel-api-conventions.md), [roadmap](../roadmap.md) step 0.3

## Context

Spec 002 builds the shared kernel (Result, validation pipeline, endpoint discovery, error contract). CLAUDE.md requires an ADR for every new library, and the project prefers what .NET, ASP.NET Core and EF Core already provide.

## Decisions

### 1. Libraries added

| Package | Why it earns its place |
|---|---|
| `Mediator.Abstractions` + `Mediator.SourceGenerator` | Intra-module command/query dispatch with a pipeline (validation) and no reflection. Source-generated, MIT. |
| `FluentValidation` + `FluentValidation.DependencyInjectionExtensions` | Request validators discovered per module assembly (`AddValidatorsFromAssembly`). |
| `Scalar.AspNetCore` | API reference UI over the built-in OpenAPI document; Development only. |
| `Microsoft.AspNetCore.OpenApi` | First-party OpenAPI document generation. |

Versions live in `Directory.Packages.props`. `Result` / `Error` are hand-rolled in Shared (about 60 lines); ErrorOr was considered and rejected: an extra dependency, and its multi-error accumulation is not needed.

### 2. Logging: no Serilog

Built-in `ILogger` with the OpenTelemetry log export already configured in ServiceDefaults gives console and Aspire dashboard logs. Serilog is not added and is removed from the stack list.

### 3. One Mediator per module, injected as the concrete class

Each module registers its own source-generated Mediator in `AddXxxModule` (`AddMediator(o => o.Namespace = "RagAssistant.Modules.<Module>.Generated")`, validation behavior in `o.PipelineBehaviors`). A spike with Mediator 3.0.2 showed that two generated mediators collide on `IMediator` / `ISender`: the first registration wins and the other module's requests fail with `MissingMessageHandlerException`. Each module's concrete generated `Mediator` class resolves correctly.

Options per module: `Namespace`, `ServiceLifetime = Scoped` (validators and DbContexts are scoped, and the default singleton fails scope validation), and `PipelineBehaviors = [typeof(ValidationBehavior<,>)]`. Because the generated class is named `Mediator`, which clashes with the library's `Mediator` namespace, files that use it alias it (`using ModuleMediator = RagAssistant.Modules.<Module>.Generated.Mediator;`).

Two more options are required once a second Mediator exists in the process (found while building the Documents module, spec 006): `GenerateTypesAsInternal = true`, because the generated `AssemblyReference` and `MediatorOptions` helper types are otherwise public in the global namespace and collide with the same types generated in any referencing project (the integration tests have their own); and `Assemblies = [typeof(<Module>Module)]`, because by default the generator scans **every referenced assembly**, so a project referencing a module would pull that module's internal handlers into its own Mediator and fail to compile.

Rule: **a module injects its own concrete generated `Mediator` and never `IMediator` / `ISender`.** Handlers stay `internal`, and a module cannot dispatch another module's requests.

## Consequences

- Module boundaries hold at the dispatcher level; cross-module calls use `Shared/Contracts` interfaces only.
- Endpoints and handlers in a module depend on a generated type, not an interface, so tests use the real Mediator rather than a mock.
- The `/scaffold` module template must carry the `AddMediator` options and the rule above.

## Rejected

| Option | Reason |
|---|---|
| Single Host-level Mediator | One dispatcher sees every module's handlers; the generator needs handlers to be public (or `InternalsVisibleTo`), weakening "internal by default". |
| ErrorOr | See decision 1. |
| Serilog | See decision 2: nothing here needs its sinks or enrichers. |
| MediatR | Reflection-based dispatch, and recent versions are commercially licensed. |
