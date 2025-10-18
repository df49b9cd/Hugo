# Roadmap & Contribution Guidelines

## Near-Term Focus

- **Select ergonomics**: evaluate additional helpers for modeling fan-in patterns on top of `Go.SelectAsync`.

## Recently Delivered

- **Result enrichments**: shipped pattern-matching helpers on `Result<T>`, JSON-friendly error serialization, and first-class optional value support.
- **Timer primitives**: `Go.After`, `Go.AfterAsync`, `Go.NewTicker`, and `Go.Tick` provide Go-like timers driven by `TimeProvider` for deterministic tests.
- **Cancellation audit**: every async combinator now propagates `Error.Canceled` with the originating token, and `Go.SelectAsync` surfaces cancellations as structured failures instead of exceptions.

## Stretch Goals

- **Cross-runtime samples**: add Azure Functions, ASP.NET Minimal APIs, and Orleans grains showcasing Hugo primitives.
- **Distributed coordination**: investigate distributed wait groups backed by Redis or Event Hubs for multi-node workloads.
- **Observability packages**: publish `Hugo.Diagnostics.OpenTelemetry` with ready-made meters and views.

## Contribution Guidelines

1. **Discuss first**: open an issue describing the problem, proposed solution, and API sketch.
2. **Design for determinism**: favour `TimeProvider` and dependency injection to keep tests deterministic.
3. **Guard null inputs**: mirror the existing guard-first style and convert exceptions to `Result<T>` failures.
4. **Cancellation-first**: every async API must accept a `CancellationToken` and propagate `Error.Canceled` consistently.
5. **Tests & coverage**: add integration and edge-case tests; run `dotnet test --collect:"XPlat Code Coverage"` before submitting a PR.
6. **Documentation**: update the relevant markdown files when you add or change primitives.
