# PA-014 – Move TaskQueueChannelAdapter Pumps Off The ThreadPool

## Goal
Prevent the adapter’s lease pumps from permanently occupying ThreadPool workers (and capturing execution context) by running them as long-running operations or via `Go.Run` so they don’t impact unrelated workloads.

## Scope
- Applies to `src/Hugo/Primitives/TaskQueue/TaskQueueChannelAdapter.cs`.
- Replace `Task.Run(PumpAsync)` + `ContinueWith` with a solution that uses dedicated threads or `TaskCreationOptions.LongRunning | DenyChildAttach` and avoids `ContinueWith` allocations.
- No changes to queue semantics.

## Requirements
1. Start each pump using `Task.Factory.StartNew(PumpAsync, ...)` or `Go.Run` with long-running semantics so blocking lease loops do not starve the ThreadPool.
2. Remove the `ContinueWith` error propagation pattern; instead, handle pump completion inside the pump or via `await Task.WhenAll`.
3. Ensure execution context is not captured unnecessarily (use `ExecutionContext.SuppressFlow()` if needed).
4. Maintain existing error handling: if a pump faults, cancel the adapter and surface the error.

## Deliverables
- Updated pump initialization and completion logic.
- Unit tests validating adapter behavior under heavy load (ensuring ThreadPool starvation doesn’t occur).
- Documentation/changelog entry.

## Acceptance Criteria
- Profiling shows pumps run on dedicated threads (or long-running tasks) without exhausting ThreadPool workers.
- No regression in adapter error handling or disposal semantics.

## References
- `src/Hugo/Primitives/TaskQueue/TaskQueueChannelAdapter.cs:20-110` – pump creation.

## Testing Strategy
### Unit Test Scenarios
- Stress test with high concurrency verifying no ThreadPool starvation (use `ThreadPool.GetAvailableThreads`).
- Fault injection to confirm errors still propagate.

### Integration Test Scenarios
- Run OmniRelay-style workloads to confirm throughput and latency remain stable under heavy leasing.

### Feature Test Scenarios
- Not applicable; document best practices for hosting pumps.

## Risks & Mitigations
- **Risk:** Dedicated threads could be overkill for low concurrency. *Mitigation:* allow configuration or fallback to Task.Run when concurrency is small.

## Observability & Diagnostics
- Consider logging pump start/stop events for monitoring.

## Rollout / Dependencies
- Communicate change to hosting teams so they plan for the new scheduling behavior.
