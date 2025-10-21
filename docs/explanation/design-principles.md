# Design Principles

Hugo adopts Go-inspired concurrency patterns while embracing .NET idioms. This document explains the reasoning behind core design choices so you can evaluate the trade-offs for your own workloads.

## Cancellation-first APIs

Every async helper accepts a `CancellationToken`. Instead of throwing `OperationCanceledException`, Hugo converts cancellations into `Error.Canceled` with metadata describing the originating token. This approach:

- Keeps result pipelines deterministic (no exceptions for control flow).
- Ensures background loops such as `SelectFanInAsync` can surface which token triggered cancellation.
- Encourages callers to propagate tokens from `TestContext.Current.CancellationToken`, improving testability.

## Deterministic timing

Go timers (`After`, `Tick`, `NewTicker`) are built on `.NET`'s `TimeProvider`. Fake providers make it possible to advance time deterministically in unit tests—a key recommendation from Microsoft’s library guidance for reliable automation.

## Structured errors

`Error` values capture a code (see `ErrorCodes`) plus a metadata bag. Wrapping exceptions with `Error.FromException` keeps stack traces while allowing pipelines to enrich context (e.g., `exceptionType`, correlation IDs). Structured errors align with observability practices and allow log sanitisation without parsing exception text.

## Channel ergonomics

Channels expose Go-style `Reader`/`Writer` pairs instead of raw `System.Threading.Channels`. Hugo layers helpers on top:

- `TryComplete(Exception?)` propagates failures reliably to downstream readers.
- `MakePrioritizedChannel` multiplexes bounded queues to prevent starvation.
- `SelectAsync` and the fluent select builder mirror Go’s `select` keyword, enabling readable fan-in logic without manual polling.

## Composable results

Railway-oriented combinators minimise nested `if`/`try` statements. By supporting both sync and async variants of `Then`, `Map`, `Tap`, and `Ensure`, pipelines remain consistent across CPU- and I/O-bound operations. The API surface mirrors functional patterns from languages like F# while staying idiomatic for C# developers.

## Observability hooks

Metrics emitted by `GoDiagnostics` expose queue depth, select latency, and wait-group usage. These measurements exist because concurrency bugs are easier to diagnose when you can correlate execution with metric spikes. The design deliberately opts into standard .NET meters so you can connect exporters without custom instrumentation. When you need turnkey exporters, the `Hugo.Diagnostics.OpenTelemetry` package wires schema-aware meters, activity sources, rate-limited sampling, and OTLP/Prometheus exporters through `builder.AddHugoDiagnostics(...)`, mirroring Aspire ServiceDefaults so hosted workers emit production-ready signals by default.

## Extensibility

Hugo favours small, composable primitives. Rather than introduce heavyweight frameworks, the library encourages extension via helper methods built on `Result<T>` and channels. This keeps the surface area approachable and aligns with the goal of evolving without frequent breaking changes—a best practice from Microsoft’s library guidance.
