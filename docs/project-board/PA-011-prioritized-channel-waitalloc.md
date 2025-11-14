# PA-011 – Reduce Allocations In PrioritizedChannel Wait Path

## Goal
Eliminate the per-wait heap allocations incurred by `PrioritizedChannelReader.WaitToReadSlowAsync`, which currently creates a `Task<bool>[]` and converts every `ValueTask<bool>` into a heap-allocated `Task` on each loop iteration.

## Scope
- `PrioritizedChannelReader.WaitToReadSlowAsync` in `src/Hugo/PrioritizedChannel.cs`.
- Focus on reusing buffers or awaiting `ValueTask<bool>` instances directly, so hot readers no longer allocate multiple tasks per wait cycle.
- No changes to writer semantics.

## Requirements
1. Replace the current `Task<bool>[]` allocation with a pooled buffer (e.g., `ArrayPool<Task<bool>>`) or restructure the loop to await `ValueTask<bool>` without boxing.
2. Avoid calling `.AsTask()` unless strictly necessary; prefer awaiting `ValueTask<bool>` inline to stay allocation-free in the common case.
3. Ensure that exceptions from individual lanes are still observed and propagated.
4. Document the perf benefits (e.g., via benchmark numbers) in the changelog.

## Deliverables
- Updated implementation with significantly fewer heap allocations per wait.
- Unit/perf tests demonstrating reduced allocations (use BenchmarkDotNet or allocation tracking tests).
- Documentation summarizing the improvement.

## Acceptance Criteria
- Allocation analysis shows near-zero allocations per wait on the common fast path.
- All priority lanes still respect cancellation/exception semantics.
- No regressions in priority ordering or throughput.

## References
- `src/Hugo/PrioritizedChannel.cs:273-337` – current slow path logic.

## Testing Strategy
### Unit Test Scenarios
- Regression tests for cancellation/exception propagation.
- Validate WaitToReadAsync behavior after pool exhaustion or re-entrancy.

### Integration Test Scenarios
- Run high-throughput channel benchmarks to confirm reduced allocations and unchanged throughput.

### Feature Test Scenarios
- Not applicable (internal change), but update docs if behavior observable.

## Risks & Mitigations
- **Risk:** Pool misuse could lead to leaks. *Mitigation:* Use `try/finally` to return buffers and document invariants.

## Observability & Diagnostics
- Optionally expose instrumentation hooks or logging to monitor allocation reductions.

## Rollout / Dependencies
- Inform downstream teams of perf improvement; no behavior change expected.
