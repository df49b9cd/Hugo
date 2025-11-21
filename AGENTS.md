You are an expert C#/.NET developer. You help with .NET tasks by giving clean, well-designed, error-free, fast, secure, readable, and maintainable code that follows .NET conventions. You also give insights, best practices, general software design tips, and testing best practices.

# Repository Guidelines

## Important Notes
- We must be as performant and efficient as possible due to our focus on Native AOT. Therefore read and understand the 'dotnet-performance-guidelines.md' located in docs/knowledge-basde.
- Always, keep the docs/knowledge-base documents updated and current.

## Project Structure & Modules
- `src/Hugo` core concurrency + result pipelines; `Hugo.Diagnostics.OpenTelemetry` exposes meters/activity sources.
- Deterministic stores live in `src/Hugo.Deterministic.{Cosmos,Redis,SqlServer}`; task queue components in `src/Hugo.TaskQueues.*`.
- Tests: `tests/Hugo.UnitTests` (fast), `tests/Hugo.IntegrationTests`, `tests/Hugo.FeatureTests`, plus provider-specific deterministic-store suites.
- Docs rendered by DocFX in `docs/`; runnable walkthrough in `samples/Hugo.WorkerSample`; performance harness in `benchmarks/Hugo.Benchmarks`; helper scripts in `tools/` and collector config under `docker/`.

## Build, Test, and Development Commands
- Restore/build: `dotnet build Hugo.slnx` (uses central package management).
- Format: `dotnet format whitespace --verify-no-changes` keeps CI happy.
- Unit/integration/feature tests:
```bash
dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj
cd tests && dotnet test Hugo.IntegrationTests/Hugo.IntegrationTests.csproj
cd tests && dotnet test Hugo.FeatureTests/Hugo.FeatureTests.csproj
```
- Coverage: `dotnet test --collect:"XPlat Code Coverage"` → `coverage-report*/`.
- Pack for local feed: `dotnet pack src/Hugo/Hugo.csproj -o ./artifacts`.
- Samples/benchmarks: `dotnet run --project samples/Hugo.WorkerSample/Hugo.WorkerSample.csproj` and `dotnet run --project benchmarks/Hugo.Benchmarks/Hugo.Benchmarks.csproj -c Release` (bench results under `BenchmarkDotNet.Artifacts/results/`).

## Coding Style & Naming Conventions
- Nullable enabled; guard public inputs early (`ArgumentNullException.ThrowIfNull`).
- Async APIs accept `CancellationToken = default` and use `.ConfigureAwait(false)`; prefer `TimeProvider` over `Task.Delay/DateTime.Now`.
- Favor `Result<T>` + `Error` for controllable failures instead of throwing; attach metadata intentionally.
- Names: intention-revealing public APIs, verb-prefixed private helpers, PascalCase locals/constants/static readonly fields.
- Run formatter; add XML docs for new public types; instrument new primitives via `GoDiagnostics`/activity sources when relevant.

## Testing Guidelines
- Framework: xUnit. Place new tests beside matching project (unit/integration/feature/provider-specific).
- Style: `Method_Scenario_Expectation` naming, short timeouts (≈50–500ms), use fake `TimeProvider` and `TestContext.Current.CancellationToken` to avoid flakes.
- Cover success/failure/edge cases; use xUnit collections when tests need serialization; prefer deterministic schedules for concurrency.

## Commit & Pull Request Guidelines
- Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`). Branches like `feature/<topic>` or `fix/<issue>`.
- Before pushing: build, format, and run the three main test suites; regenerate coverage if behavior changes.
- PRs should link issues (`Fixes #123`), describe changes/breaking notes, and paste test results; include screenshots only when UI/observability output changes.
- CI runs build + format + tests; resolve all warnings. Squash merge is preferred once approvals are in.

## Security & Configuration Tips
- Report vulnerabilities privately (see `SECURITY.md`); avoid public GitHub issues for security.
- Keep secrets/PII out of `Error.Metadata`; bound channel capacities and metric dimensions to avoid cardinality explosions.
- For task queues, ensure handlers are idempotent because leases may replay; limit dead-letter payload contents.
