# Runtime Components

Purpose: what executes in-process and where to extend it.

## Source map
- `src/Hugo/Primitives` - channels, wait groups, mutex/RW lock, timers, select plumbing, prioritized channels, deferred cleanup.
- `src/Hugo/Go` & `src/Hugo/Go/Select` - ergonomic helpers (`MakeChannel`, `SelectAsync`, `FanInAsync`, `WaitGroup`, cancellation defaults) plus select cases.
- `src/Hugo/Results` & `Functional.cs` - `Result<T>` pipelines, streaming combinators, fallbacks, sagas, validation helpers, partial application, optional values.
- `src/Hugo/Policies` - retry/timeout/circuit-style helpers tuned for `Result<T>` and `CancellationToken` aware flows.
- `src/Hugo/Deterministic` - `DeterministicEffectStore`, `DeterministicGate`, `VersionGate`, replay/upgrade semantics, captured side effects, JSON contexts.
- `src/Hugo/TaskQueues` - safe task queues with leasing, backpressure thresholds, dead-letter handling, health checks, heartbeat/renewal utilities.
- `src/Hugo/Workflow` & `src/Hugo/Sagas` - orchestration helpers that tie results, deterministic gates, and task queues together.
- `src/Hugo/TimeProvider*` - abstractions over delay and timers to make scheduling deterministic and testable.

## Responsibilities
- Go-like concurrency primitives implemented with allocation discipline and cooperative cancellation.
- Functional result pipelines that avoid exceptions for control flow and compose with async/ValueTask paths.
- Deterministic state capture for workflows that must replay across processes/providers without double execution.
- Queueing infrastructure that enforces leases, offers backpressure signals, and remains observable.

## Backlog references
- File runtime changes with clear hot-path impact; benchmark or profile before/after using `benchmarks/Hugo.Benchmarks` or `tools/profiling-toolkit/*`.
- Use provider-specific tests (`tests/Hugo.Deterministic.*.Tests`) to guard deterministic APIs when adding storage behaviors or leak fixes.
- Apply the perf checklist from `docs/perf/dotnet-performance-guidelines.md` to any new primitive or pipeline API.

## Cross-cutting relationships
- `GoDiagnostics` is invoked throughout primitives, queues, and deterministic flows; keep new runtime code emitting the right counters/activities.
- `Result<T>` is the contract between concurrency pieces and higher-level orchestration; new APIs should prefer results over throwing.
- `TimeProvider` abstractions are required for reproducible tests-avoid `Task.Delay`/`DateTime.UtcNow` in runtime components.
- Task queues and deterministic gates share concepts (leases, effect payloads); changes to one often require updates to provider implementations and replication packages.
