# PA-013 – Bound TaskQueueChannelAdapter In-Flight Lease Count

## Goal
Ensure `TaskQueueChannelAdapter` never pulls more leases than downstream consumers can handle by bounding the number of outstanding items and preventing unbounded growth of `_leaseChannel` and `_queue._leases` when readers are slow or blocked.

## Scope
- Applies to `src/Hugo/Primitives/TaskQueue/TaskQueueChannelAdapter.cs`.
- Introduce backpressure so each pump only holds at most one lease (or a configurable limit) until the consumer reads it, keeping the adapter’s concurrency aligned with actual consumption.
- No changes to `TaskQueue<T>` leasing semantics beyond avoiding runaway leases.

## Requirements
1. Replace the unbounded `leaseChannel` or pump loop so only `concurrency` leases may be outstanding—e.g., use a bounded channel with capacity equal to concurrency or gate pumps with a semaphore.
2. Prevent pumps from draining `_queue.LeaseAsync` while `_leaseChannel.Writer` is full—avoid `WaitToWriteAsync` loops that pull another lease before the previous is delivered.
3. Ensure cancellation/disposal still requeues active leases deterministically.
4. Document the change, including any new configuration knobs for channel capacity.

## Deliverables
- Refactored adapter implementation with bounded in-flight leases.
- Unit tests showing that slow readers no longer cause `_leases.Count` to grow beyond `concurrency`.
- Updated documentation/changelog.

## Acceptance Criteria
- With `concurrency = N`, the adapter never holds more than `N` active leases regardless of consumer speed.
- Throughput for healthy consumers remains the same as before.
- No regressions in disposal or requeue behavior.

## References
- `src/Hugo/Primitives/TaskQueue/TaskQueueChannelAdapter.cs:20-134` – pump creation and publishing.
- `src/Hugo/Primitives/TaskQueue.cs:468-640` – lease lifecycle.

## Testing Strategy
### Unit Test Scenarios
- Simulate slow consumers; verify `_queue.ActiveLeaseCount` <= `concurrency`.
- Cancellation/disposal tests to ensure leases are requeued/failed cleanly.

### Integration Test Scenarios
- Run SafeTaskQueue workloads via the adapter with high concurrency and artificially slow readers; confirm no runaway lease growth.

### Feature Test Scenarios
- Update docs/sample host to showcase bounded adapters; run sample to verify behavior.

## Risks & Mitigations
- **Risk:** Smaller buffer might reduce throughput under certain schedulers. *Mitigation:* Allow overriding channel capacity but keep safe default.

## Observability & Diagnostics
- Consider logging a warning if consumers stall and all pumps wait due to bounded channel.

## Rollout / Dependencies
- Notify OmniRelay teams so they depend on bounded semantics when upgrading.
