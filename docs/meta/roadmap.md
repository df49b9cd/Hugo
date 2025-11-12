# Roadmap & Contribution Guidelines

## In progress

- **Structured API reference**: Expand XML docs and integrate with DocFX to keep the reference section in sync with the codebase.

## Recently shipped

- **Task queue channel adapter**: Introduced `TaskQueueChannelAdapter<T>` with lease requeue semantics, background pump fault handling, and a serialized xUnit collection to stabilise `TaskQueue` timing tests.
- **Playbook templates**: Documented timeout, retry, and cancellation patterns for HTTP calls, queue handlers, and sagas in `docs/how-to/playbook-templates.md`.
- **Diagnostics exporters**: Shipped `Hugo.Diagnostics.OpenTelemetry` with schema-aware meters, `ActivitySourceOptions` wiring, rate-limited sampling, OTLP/Prometheus exporters, and Aspire ServiceDefaults extensions so Hugo apps emit production-ready signals out-of-the-box.
- **Diagnostics & observability hardening**: Adopted telemetry schema URLs, rate-limited sampling helpers, and Aspire integration guidance so Hugo diagnostics align with modern OpenTelemetry practice; documented OTLP exporter defaults for workflow metrics.
- **Workflow visibility & search**: Published `docs/how-to/workflow-visibility.md` covering canonical attributes, storage layouts, and query recipes powered by `WorkflowExecutionContext`.
- **ErrGroup orchestration**: Added `Hugo.ErrGroup` for cancellable task orchestration with `WaitGroup` integration and structured error propagation.
- **Select builder upgrades**: `Go.Select(...)` now supports default branches, priority ordering, and deadline helpers for deterministic channel coordination.
- **Profiling toolkit**: Documented dotnet diagnostic recipes (`docs/how-to/profiling-toolkit.md`), shipped helper scripts and collection rule templates under `tools/profiling-toolkit`, and published a GitHub Actions baseline workflow for automated trace captures.
- **Result enrichments**: Pattern-matching helpers, JSON-friendly error serialization, and optional value support.
- **Timer primitives**: `Go.After`, `Go.AfterAsync`, `Go.NewTicker`, and `Go.Tick` built on `TimeProvider` for deterministic tests.
- **Cancellation audit**: Async combinators propagate `Error.Canceled` with origin metadata; `Go.SelectAsync` surfaces structured failures.
- **Select ergonomics**: Added `Go.SelectFanInAsync`, `Go.FanInAsync`, and `Go.FanIn` for ergonomic fan-in workflows.
- **Channel builders**: Fluent factories and DI helpers for bounded and prioritised channels.
- **Workflow execution context & metrics**: Introduced ambient `WorkflowExecutionContext`, visibility records, and workflow-centric counters/histograms/activity spans in `GoDiagnostics` to surface logical clocks, replay counts, and status transitions.
- **Functional pipeline enhancements**: Delivered `Result.WhenAll/WhenAny`, saga builders with compensation support, retry policies, and streaming/partitioning combinators that bridge `IAsyncEnumerable<T>` with channels and results for deterministic fan-in/out patterns.
- **Deterministic coordination**: `DeterministicGate.Workflow` orchestrates versioned branches with scoped effect capture, replay-safe exception/cancellation handling, and documentation covering replay strategies.
- **Result pipeline orchestration**: Added `Result.TieredFallbackAsync`, `ResultFallbackTier<T>`, and errgroup-aware `Go` overloads so retry, compensation, and tiered fallback guidance is one place, with docs covering cancellation metadata and observability.

## Backlog

- **Cross-runtime samples**: Azure Functions, ASP.NET minimal APIs, and Orleans grains that showcase Hugo primitives.
- **Distributed coordination**: Investigate Redis- or Event Hubs-backed wait groups for multi-node workloads.

## Contribution guidelines

1. **Discuss first**: Open an issue describing the problem, proposed solution, and API sketch.
2. **Design for determinism**: Prefer `TimeProvider` and dependency injection to keep tests repeatable.
3. **Guard null inputs**: Follow the guard-first style and convert exceptions to `Result<T>` failures.
4. **Cancellation-first**: Every async API must accept a `CancellationToken` and propagate `Error.Canceled` consistently.
5. **Tests & coverage**: Add integration and edge-case tests; run all suites (`tests/Hugo.UnitTests`, `tests/Hugo.IntegrationTests`, `tests/Hugo.FeatureTests`) with `dotnet test --collect:"XPlat Code Coverage"` before submitting a PR.
6. **Documentation**: Update tutorials, how-to guides, and reference pages alongside code changes.
