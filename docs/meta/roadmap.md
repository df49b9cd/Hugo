# Roadmap & Contribution Guidelines

## In progress

- **Structured API reference**: Expand XML docs and integrate with DocFX to keep the reference section in sync with the codebase.
- **Channel builders**: Provide fluent factories for prioritised and bounded channels to simplify DI registration.
- **Diagnostics exporters**: Prototype `Hugo.Diagnostics.OpenTelemetry` with opinionated views and histogram boundaries.
- **Profiling toolkit**: Document dotnet diagnostic recipes (`docs/how-to/profiling-toolkit.md`), ship helper scripts and collection rule templates under `tools/profiling-toolkit`, capture reproducible traces with `dotnet-trace`, watch runtime counters with `dotnet-counters`, and surface automated collection rules via `dotnet-monitor` to detect throughput and GC regressions. **Status:** GitHub Actions baseline workflow available; iterate on additional automation samples as needed.

## Recently shipped

- **Result enrichments**: Pattern-matching helpers, JSON-friendly error serialization, and optional value support.
- **Timer primitives**: `Go.After`, `Go.AfterAsync`, `Go.NewTicker`, and `Go.Tick` built on `TimeProvider` for deterministic tests.
- **Cancellation audit**: Async combinators propagate `Error.Canceled` with origin metadata; `Go.SelectAsync` surfaces structured failures.
- **Select ergonomics**: Added `Go.SelectFanInAsync`, `Go.FanInAsync`, and `Go.FanIn` for ergonomic fan-in workflows.

## Backlog

- **Cross-runtime samples**: Azure Functions, ASP.NET minimal APIs, and Orleans grains that showcase Hugo primitives.
- **Distributed coordination**: Investigate Redis- or Event Hubs-backed wait groups for multi-node workloads.
- **Playbook templates**: Publish recommended timeout, retry, and cancellation patterns for common distributed tasks.

## Contribution guidelines

1. **Discuss first**: Open an issue describing the problem, proposed solution, and API sketch.
2. **Design for determinism**: Prefer `TimeProvider` and dependency injection to keep tests repeatable.
3. **Guard null inputs**: Follow the guard-first style and convert exceptions to `Result<T>` failures.
4. **Cancellation-first**: Every async API must accept a `CancellationToken` and propagate `Error.Canceled` consistently.
5. **Tests & coverage**: Add integration and edge-case tests; run `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj --collect:"XPlat Code Coverage"` before submitting a PR.
6. **Documentation**: Update tutorials, how-to guides, and reference pages alongside code changes.
