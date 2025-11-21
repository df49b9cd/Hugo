# Architecture Overview

Purpose: fast orientation to Hugo's layers, where to look in the repo, and how pieces compose.

## Source map
- `src/Hugo` - core library: Go-style primitives (`Primitives`, `Go`, `SelectBuilder`, `ChannelBuilders`, `TimerChannel`), result pipelines (`Results`, `Functional`, `Optional`), policies/sagas (`Policies`, `Sagas`), deterministic coordination (`Deterministic`, `Workflow`), task queues (`TaskQueues`), diagnostics (`GoDiagnostics`), and hosting helpers (`TimeProvider*`).
- `src/Hugo.Diagnostics.OpenTelemetry` - OpenTelemetry glue: meters/activity sources exposure and DI extensions.
- `src/Hugo.Deterministic.{Cosmos,Redis,SqlServer}` - deterministic effect store providers; align with the `DeterministicEffectStore` abstractions in `src/Hugo/Deterministic`.
- `src/Hugo.TaskQueues.{Diagnostics,Replication}` - optional packages that add queue-level metrics/log shipping and cross-node replication contracts.
- `samples/` - runnable compositions: `Hugo.WorkerSample` (baseline worker), `Hugo.TaskQueueDiagnosticsHostSample`, plus deterministic worker variants.
- `tests/` - unit/integration/feature suites plus provider-specific deterministic store tests; shared test shims in `tests/Shared`.
- `benchmarks/Hugo.Benchmarks` - performance harness for hot-path regression checks.
- `docs/` - tutorials, references, perf guidance (`docs/perf/dotnet-performance-guidelines.md`), and these knowledge base notes.

## Responsibilities
- Deliver allocation-aware concurrency primitives and result pipelines that stay AOT/trimming friendly.
- Provide deterministic workflow constructs (version gates, effect stores) that can replay safely across providers.
- Ship task queue infrastructure with leasing, backpressure, and diagnostics hooks.
- Expose first-party metrics and tracing surfaces that plug into OpenTelemetry without extra wrapping.

## Backlog references
- Track architecture-impacting work in GitHub issues; tag items by area (`concurrency`, `results`, `deterministic`, `taskqueue`, `diagnostics`) so ownership is obvious.
- Use `benchmarks/Hugo.Benchmarks` or profiling toolkit baselines as regression gates for perf-linked backlog items.
- Before booking AOT or trimming changes, reread `docs/perf/dotnet-performance-guidelines.md` and budget the impact in CI.

## Cross-cutting relationships
- `GoDiagnostics` is the shared instrumentation surface consumed by `Hugo.Diagnostics.OpenTelemetry` and queue/replication packages.
- Deterministic stores implement the abstractions under `src/Hugo/Deterministic`; they are pulled in by both `Workflow` helpers and task queue replay paths.
- Task queues, result pipelines, and select/fan-in helpers are designed to run under fake `TimeProvider` for reproducible tests; use this in new features by default.
- Samples and benchmarks are the canonical integration points; mirroring their wiring keeps new features aligned with hosting guidance and CI expectations.

## Fast-start reads
- Product overview in `README.md`.
- Per-area behavior in `docs/reference/*.md` and recipes in `docs/how-to/`.
- Perf posture in `docs/perf/dotnet-performance-guidelines.md`.
