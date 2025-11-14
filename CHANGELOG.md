# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- MIT license for open-source distribution
- Comprehensive package metadata for NuGet publishing
- Code coverage reporting in CI pipeline
- SECURITY.md for vulnerability disclosure
- CONTRIBUTING.md with development guidelines

### Changed

- Improved README with license information and benchmark results
- ErrGroup now throws `ObjectDisposedException` for any `Go(...)` call after disposal, preventing background failures when reusing disposed instances
- Manual calls to `ErrGroup.Cancel()` now record a structured cancellation error so `WaitAsync` deterministically reports the group as aborted

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
