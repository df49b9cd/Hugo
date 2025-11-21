# Build & CI

Purpose: what keeps the repo green and how to reproduce it locally.

## Source map
- `Hugo.slnx` - solution entry point used by all build/test invocations.
- `.github/workflows/docker-ci.yml` - main CI: builds Docker image, runs tests, packs NuGet artifacts, publishes coverage, and (on tags) pushes packages/releases.
- `.github/workflows/profiling-baseline.yml` - optional workflow to capture profiling baselines via `tools/profiling-toolkit`.
- `docker/ci/Dockerfile.ci` - multi-stage image that restores, builds, tests, and packs with consistent SDKs.
- `build-warnings.log` - local snapshot of outstanding build warnings (keep empty in main).

## Core commands
- Restore/build: `dotnet build Hugo.slnx` (central package management).
- Format gate: `dotnet format whitespace --verify-no-changes`.
- Tests: `dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj`; then `cd tests && dotnet test Hugo.IntegrationTests/Hugo.IntegrationTests.csproj`; then `cd tests && dotnet test Hugo.FeatureTests/Hugo.FeatureTests.csproj` (plus provider suites as needed).
- Coverage: `dotnet test --collect:"XPlat Code Coverage"` -> reports under `coverage-report*/`.
- Pack locally: `dotnet pack src/Hugo/Hugo.csproj -o ./artifacts`.
- Benchmarks: `dotnet run --project benchmarks/Hugo.Benchmarks/Hugo.Benchmarks.csproj -c Release`.

## Backlog references
- CI failures should be reproduced with the docker CI image; update `docker/ci/Dockerfile.ci` only when SDK/tooling changes land.
- Treat new build warnings as regressions-fix or suppress with justification before merging.
- For publishing changes, ensure tagging flow in `docker-ci` still pushes NuGet/GitHub packages; document any required secrets/permissions in issues/PRs.

## Cross-cutting relationships
- Perf/AOT work must keep CI green; prefer adding benchmarks when touching hot paths to avoid regressions slipping past tests.
- Formatting is part of CI gating-run it before pushing to avoid noisy PRs.
- Coverage artifacts are uploaded by CI; when adding new assemblies ensure the docker image copies resulting reports from `/repo/artifacts` as expected.
