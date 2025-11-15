# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- Converted the Go concurrency surface (`Go.Select*`, fan-in/out helpers, retries/timeouts, timers, and top-level `Go.Run`) to return `ValueTask`/`ValueTask<T>` instead of `Task`, aligning with .NET guidance for allocation-sensitive flows.
- Replaced `ChannelCase` with the generic `ChannelCase<TResult>` so select continuations no longer rely on `Result<Go.Unit>` sentinels. All `ChannelCase` factories now accept ValueTask-based delegates, and `SelectBuilder.ExecuteAsync` returns `ValueTask<Result<TResult>>`.

### Added

- `ResultPipelineChannels.SelectAsync` / `.FanInAsync` / `.MergeAsync` / `.BroadcastAsync` bridge the Go channel helpers with pipeline-aware cancellation tokens, time providers, and compensation scopes.
- `ResultPipeline.FanOutAsync`, `.RaceAsync`, `.RetryAsync`, and `.WithTimeoutAsync` provide `ValueTask<Result<T>>` wrappers over the result pipeline orchestrators so callers can reuse Go-style ergonomics without losing diagnostics.
- `Result<T>.WithCompensation(...)` plus a `ResultPipelineStepContext.CancellationToken` accessor make it easier for ad-hoc continuations to emit rollback actions that orchestrators absorb automatically.
- MIT license for open-source distribution
- Comprehensive package metadata for NuGet publishing
- Code coverage reporting in CI pipeline
- SECURITY.md for vulnerability disclosure
- CONTRIBUTING.md with development guidelines
- Configurable prioritized channel prefetch limits via `PrioritizedChannelOptions.PrefetchPerPriority` / `PrioritizedChannelBuilder.WithPrefetchPerPriority` to balance throughput and backpressure

### Changed

- Improved README with license information and benchmark results
- ErrGroup now throws `ObjectDisposedException` for any `Go(...)` call after disposal, preventing background failures when reusing disposed instances
- Manual calls to `ErrGroup.Cancel()` now record a structured cancellation error so `WaitAsync` deterministically reports the group as aborted
- Policy-backed `ErrGroup.Go` overloads now cancel peer work before compensation completes so slow cleanup no longer delays cancellation propagation
- `Result.WhenAll` now replays compensation for partially completed operations when cancellation occurs (including when `Task.WhenAll` throws `OperationCanceledException`), ensuring side effects are rolled back before returning `Error.Canceled`
- `Go.Run` and `WaitGroup.Go` accept custom `TaskScheduler`/`TaskCreationOptions` hints and can now track existing `Task`/`ValueTask` instances without paying an extra `Task.Run` allocation
- Functional result combinators reuse the originating failure via `Result<T>.CastFailure<TOut>()`, removing redundant allocations and duplicate `result.failures` diagnostics when propagating errors
- `TaskQueueChannelAdapter` now bounds its internal channel to `concurrency` and waits for capacity before leasing, so slow consumers cannot inflate `_queue.ActiveLeaseCount`
- `ErrGroup` manual cancellations now set `Error.Canceled` before signaling the linked token and pipeline failures notify peers prior to running compensation, preventing “success” results after aborts
- `PrioritizedChannelReader` avoids allocating `Task<bool>[]` arrays and `.AsTask()` conversions on each wait by reusing lightweight registrations, eliminating per-iteration heap churn on the slow path (PA-011)

### Fixed

- `Result.WhenAny` now deterministically returns the first successful result even if other operations fail or cancel after the winner is selected.
- `PrioritizedChannelReader` stops draining entire priority queues into its buffer, ensuring `BoundedChannelFullMode` and per-level capacity apply under sustained load (PA-010).
- `PrioritizedChannelReader.WaitToReadAsync` now observes and rethrows exceptions or cancellations from individual priority lanes, eliminating `UnobservedTaskException` crashes when one lane faults (PA-012).

## [1.0.0] - 2025-10-21

### Added

- Go-style concurrency primitives (`WaitGroup`, `Mutex`, `RwMutex`, `Once`, `Pool`)
- Channel builders and prioritized channels for message passing
- Railway-oriented `Result<T>` pipelines with functional combinators
- Task queue with cooperative leasing, heartbeats, and dead-lettering
- Select helpers for awaiting multiple channel operations
- Fan-in and fan-out utilities for channel coordination
- Timer primitives (`After`, `NewTicker`, `Tick`) with `TimeProvider` support
- Deterministic coordination (`VersionGate`, `DeterministicEffectStore`)
- Saga orchestration with compensation and retry policies
- Tiered fallback strategies for resilience patterns
- ErrGroup for structured concurrent error handling
- Comprehensive diagnostics with OpenTelemetry integration
- `Hugo.Diagnostics.OpenTelemetry` package with schema-aware meters and exporters
- Workflow execution context with replay support
- Structured error types with metadata and error codes
- Full test suite with xUnit v3, property-based testing with FsCheck
- Benchmark suite comparing Hugo primitives against .NET equivalents
- Complete documentation following Diátaxis framework
  - Tutorials (Getting Started)
  - How-to guides (Fan-in, OpenTelemetry, Playbooks, Profiling, Workflow Visibility)
  - Reference (Concurrency Primitives, Result Pipelines, Diagnostics, Deterministic Coordination)
  - Explanation (Design Principles)
- Worker sample demonstrating task queue integration
- CI pipeline with GitHub Actions
- Profiling baseline workflow for performance tracking

### Changed

- Multi-targeted for .NET 9.0 and .NET 10.0

### Fixed

- N/A (initial release)

[Unreleased]: https://github.com/df49b9cd/Hugo/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/df49b9cd/Hugo/releases/tag/v1.0.0
