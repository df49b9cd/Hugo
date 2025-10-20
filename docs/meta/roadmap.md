# Roadmap & Contribution Guidelines

## In progress

- **Structured API reference**: Expand XML docs and integrate with DocFX to keep the reference section in sync with the codebase.
- **Diagnostics exporters**: Prototype `Hugo.Diagnostics.OpenTelemetry` with schema-aware meters (`Meter.TelemetrySchemaUrl`), `ActivitySourceOptions`, and Aspire ServiceDefaults so Hugo apps emit OTLP/Prometheus friendly signals out-of-the-box.
- **Result pipeline orchestration**: Nail down retry/compensation story across `Result<T>` and `ErrGroup`, including guidance for tiered fallbacks and cancellation surfacing.
- **Workflow visibility & search**: Define attribute schema, storage guidance, and query patterns that take advantage of the new workflow execution context metadata.

## Recently shipped

- **Task queue channel adapter**: Introduced `TaskQueueChannelAdapter<T>` with lease requeue semantics, background pump fault handling, and a serialized xUnit collection to stabilise `TaskQueue` timing tests.
- **ErrGroup orchestration**: Added `Hugo.ErrGroup` for cancellable task orchestration with `WaitGroup` integration and structured error propagation.
- **Select builder upgrades**: `Go.Select(...)` now supports default branches, priority ordering, and deadline helpers for deterministic channel coordination.
- **Profiling toolkit**: Documented dotnet diagnostic recipes (`docs/how-to/profiling-toolkit.md`), shipped helper scripts and collection rule templates under `tools/profiling-toolkit`, and published a GitHub Actions baseline workflow for automated trace captures.
- **Result enrichments**: Pattern-matching helpers, JSON-friendly error serialization, and optional value support.
- **Timer primitives**: `Go.After`, `Go.AfterAsync`, `Go.NewTicker`, and `Go.Tick` built on `TimeProvider` for deterministic tests.
- **Cancellation audit**: Async combinators propagate `Error.Canceled` with origin metadata; `Go.SelectAsync` surfaces structured failures.
- **Select ergonomics**: Added `Go.SelectFanInAsync`, `Go.FanInAsync`, and `Go.FanIn` for ergonomic fan-in workflows.
- **Channel builders**: Fluent factories and DI helpers for bounded and prioritised channels.
- **Workflow execution context & metrics**: Introduced ambient `WorkflowExecutionContext`, visibility records, and workflow-centric counters/histograms/activity spans in `GoDiagnostics` to surface logical clocks, replay counts, and status transitions.

## Backlog

- **Cross-runtime samples**: Azure Functions, ASP.NET minimal APIs, and Orleans grains that showcase Hugo primitives.
- **Distributed coordination**: Investigate Redis- or Event Hubs-backed wait groups for multi-node workloads.
- **Playbook templates**: Publish recommended timeout, retry, and cancellation patterns for common distributed tasks.
- **Deterministic coordination**: Extend `DeterministicGate` into a full version/side-effect gating workflow and document replay strategies informed by deterministic replay requirements.
- **Functional pipeline enhancements**: Add `Result.WhenAll/WhenAny`, saga-style compensation helpers, resilient retry wrappers, and streaming/partitioning combinators that bridge `IAsyncEnumerable<T>` with channels/results for deterministic fan-in/out.
- **Diagnostics & observability hardening**: Adopt telemetry schema URLs, rate-limited sampling, and Aspire dashboard integration so Hugo diagnostics align with modern OpenTelemetry guidance; document storage/OTLP exporter defaults leveraging the enriched workflow metrics.

## Contribution guidelines

1. **Discuss first**: Open an issue describing the problem, proposed solution, and API sketch.
2. **Design for determinism**: Prefer `TimeProvider` and dependency injection to keep tests repeatable.
3. **Guard null inputs**: Follow the guard-first style and convert exceptions to `Result<T>` failures.
4. **Cancellation-first**: Every async API must accept a `CancellationToken` and propagate `Error.Canceled` consistently.
5. **Tests & coverage**: Add integration and edge-case tests; run `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj --collect:"XPlat Code Coverage"` before submitting a PR.
6. **Documentation**: Update tutorials, how-to guides, and reference pages alongside code changes.
