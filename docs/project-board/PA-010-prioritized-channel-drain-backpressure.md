# PA-010 – Preserve Backpressure In PrioritizedChannel Reader

## Goal
Prevent `PrioritizedChannelReader` from draining entire priority queues into the internal buffer, so per-level capacity and `BoundedChannelFullMode` are respected under load.

## Scope
- Applies to `PrioritizedChannel<T>.PrioritizedChannelReader` in `src/Hugo/PrioritizedChannel.cs`.
- Focus on `WaitToReadAsync`/`WaitToReadSlowAsync` logic that currently calls `TryRead` in a loop and enqueues all items into `_buffer`.
- No changes to writer behavior or unrelated channels.

## Requirements
1. Update `WaitToReadAsync` and `WaitToReadSlowAsync` so they enqueue at most one (or a bounded batch) item per priority when signaling readiness, maintaining pressure on the underlying lanes.
2. Ensure `_buffer` capacity remains bounded (or at least limited to a configurable threshold) to avoid duplicating all queued work.
3. Preserve the priority ordering guarantee: higher-priority lanes still get dequeued first.
4. Provide documentation explaining the new behavior and any configuration knobs.

## Deliverables
- Adjusted reader implementation respecting backpressure.
- Unit tests covering high-throughput scenarios where writers should block once capacity is reached.
- Documentation/changelog updates summarizing the fix.

## Acceptance Criteria
- Writing to a bounded priority lane blocks once its capacity is reached, even when consumers are slow, because the reader no longer drains entire lanes.
- Existing tests for priority ordering remain green; new tests cover the regression.
- No unbounded growth in `_buffer` during benchmarks.

## References
- `src/Hugo/PrioritizedChannel.cs:250-320` – `WaitToReadAsync` and slow path.
- `src/Hugo/ChannelBuilders.cs` – capacity/full-mode configuration.

## Testing Strategy
### Unit Test Scenarios
- `PrioritizedChannel_ShouldRespectCapacity_WhenReaderSlow`: ensure writers block when capacity reached.
- `PrioritizedChannel_ShouldDrainSingleItemPerLane`: verify buffer only contains limited number of items.
- Regression tests for priority ordering.

### Integration Test Scenarios
- Use channel in a simulated load (e.g., queue processing) to confirm throughput/backpressure behave as expected.

### Feature Test Scenarios
- Update documentation sample and run it to confirm blocking behavior matches description.

## Risks & Mitigations
- **Risk:** Restricting buffer may reduce throughput. *Mitigation:* Allow small configurable batch size; document trade-offs.

## Observability & Diagnostics
- Consider adding diagnostics counters for buffer depth per priority.

## Rollout / Dependencies
- Coordinate with downstream components relying on current draining behavior to ensure they adopt the change smoothly.
