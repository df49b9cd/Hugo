# Testing Strategy

Purpose: how we test Hugo and where to place coverage.

## Source map
- `tests/Hugo.UnitTests` - fast feedback on primitives, results, policies, diagnostics invariants.
- `tests/Hugo.IntegrationTests` - multi-component workflows and hosting setups.
- `tests/Hugo.FeatureTests` - scenario/behavioural coverage for public surface areas.
- `tests/Hugo.Deterministic.*.Tests` - provider-specific suites for Cosmos, Redis, SQL Server deterministic stores.
- `tests/Shared` - xUnit shims and deterministic test helpers (`DeterministicTestSkipper`, `XunitTestingPlatformShim`).
- `benchmarks/Hugo.Benchmarks` - perf regression guardrail; treat key benches as executable specs for hot paths.

## Responsibilities
- Keep async/concurrency tests deterministic using fake `TimeProvider` and `TestContext.Current.CancellationToken`.
- Validate success/failure/edge cases for `Result<T>` pipelines and queue workflows; prefer results over exception assertions.
- Run unit/integration/feature suites locally (`dotnet test` on each csproj) before PRs; keep tests short (~50-500 ms timeouts).

## Backlog references
- File gaps by area (channels, select, task queues, deterministic stores, diagnostics) with repros; add tests next to the feature's project.
- Add regression tests whenever perf fixes land-benchmark first, then add targeted tests or micro-benches for the hot path.
- Apply naming convention `Method_Scenario_Expectation` and guard public inputs with `ArgumentNullException.ThrowIfNull` in new APIs.

## Cross-cutting relationships
- New telemetry or serialization behaviors should be covered in provider tests and documented in `docs/reference/*` to stop drift.
- Use `TimeProvider` abstractions in all timing-sensitive tests; avoid `Task.Delay`/`DateTime.UtcNow` to keep CI deterministic.
- When adding integration flows, mirror the wiring from `samples/` to keep tests representative of real hosts.
