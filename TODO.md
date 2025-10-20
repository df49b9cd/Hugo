# Project TODO

## Concurrency upgrades

- [x] Design API surface for `ErrGroup`-style orchestration (cancellation + shared error propagation) and spike implementation in `src/Hugo/Primitives`.
- [ ] Extend `SelectBuilder` to support default cases, priority ordering, and deadline helpers while maintaining deterministic replay semantics.
- [ ] Prototype deterministic side-effect/version-gating utilities that persist recorded outputs for replay-safe workflows.
- [ ] Draft channel-backed task queue abstraction (poll loops, leasing, heartbeats) and integration tests for workflow/activity workers.

## Functional pipeline enhancements

- [ ] Implement `Result.WhenAll`/`Result.WhenAny` helpers with policy-driven retry/compensation hooks.
- [ ] Add saga/compensation pipeline module with automatic rollback semantics for multi-step activities.
- [ ] Bridge `IAsyncEnumerable<T>` with channels/results via streaming combinators (fan-in/out, windowing, partitioning).
- [ ] Provide higher-order operators (`Group`, `Partition`, `Window`) and resilient retry wrappers aligned with schedule/cron workflows.

## Determinism & observability support

- [ ] Define workflow execution context object exposing logical clock, schedule identifiers, and namespace metadata.
- [ ] Expand `GoDiagnostics` to emit task-queue depth, replay counters, and workflow status metrics; add documentation and exporters.
- [ ] Plan visibility/search attribute strategy leveraging new diagnostics for advanced querying.
