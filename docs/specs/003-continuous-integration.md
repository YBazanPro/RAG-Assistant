# Spec 003 — Continuous integration

**Roadmap:** step 0.4

## Goal
Every pull request and every push to `main` is validated automatically.

## Acceptance criteria
1. A GitHub Actions workflow (`.github/workflows/ci.yml`) runs on `pull_request` and on `push` to `main`.
2. It restores, builds in Release (warnings are errors), verifies formatting (`dotnet format --verify-no-changes`) and runs `dotnet test --solution RagAssistant.slnx` on `ubuntu-latest`.
3. Integration tests run against PostgreSQL through Testcontainers using the runner's Docker.
4. The SDK version comes from `global.json`; NuGet packages are cached.
5. A new push to the same PR cancels the previous run.
6. Test results are uploaded as an artifact when the job fails.
7. The workflow has read-only permissions and uses no secrets.

## Out of scope
Deployment, coverage gates, the Keycloak smoke test (added to the standard test run by [spec 004](004-identity.md)), dependency update bots.
