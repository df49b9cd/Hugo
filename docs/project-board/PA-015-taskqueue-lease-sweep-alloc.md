# PA-015 – Reduce Allocation & CPU Overhead In TaskQueue Lease Sweeps

## Goal
Make the lease-sweep path scale to thousands of active leases by reducing the O(n) allocation/copy overhead currently incurred on every sweep interval.

## Scope
- `SweepExpiredLeasesAsync`, `RequeueAsync`, and supporting helpers in `src/Hugo/Primitives/TaskQueue.cs`.
- Focus on trimming allocations (List<(Guid, LeaseState)>, Dictionary metadata per lease) and minimizing per-sweep work when no leases expire.

## Requirements
1. Short-circuit the sweep when `_leases.Count == 0` to avoid unnecessary dictionary iteration.
2. Replace the `List<(Guid, LeaseState)>` creation with a pooled buffer or enumerate/act in-place to avoid duplicating the expired set.
3. Only allocate metadata dictionaries when diagnostics/backpressure is enabled; otherwise reuse or share objects.
4. Document the performance improvement (before/after metrics) in changelog.

## Deliverables
- Optimized sweep implementation.
- Perf benchmarks demonstrating reduced allocations/CPU when running with thousands of leases.
- Tests ensuring behavior remains correct (leases still expire and requeue appropriately).

## Acceptance Criteria
- Allocation profiling shows near-zero GC allocations per sweep when no leases expire.
- CPU usage per sweep is significantly reduced for large lease counts.
- Existing functionality (expiration, requeue, dead-letter) remains intact.

## References
- `src/Hugo/Primitives/TaskQueue.cs:560-640` – lease sweep logic.

## Testing Strategy
### Unit Test Scenarios
- Verify sweeps skip work when `_leases.Count == 0`.
- Ensure metadata is attached only when diagnostics require it.

### Integration Test Scenarios
- Benchmarks with thousands of leases to compare before/after allocations.

### Feature Test Scenarios
- Not applicable; document change in release notes.

## Risks & Mitigations
- **Risk:** Reusing buffers might introduce concurrency bugs. *Mitigation:* Use thread-safe pooling or allocate per-sweep but from ArrayPool.

## Observability & Diagnostics
- Add optional counters for sweep duration to track improvements.

## Rollout / Dependencies
- Coordinate with teams running high-volume queues to validate the improvements.
