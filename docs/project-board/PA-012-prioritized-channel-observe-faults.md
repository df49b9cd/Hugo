# PA-012 – Observe Exceptions From PrioritizedChannel Wait Tasks

## Goal
Ensure `PrioritizedChannelReader.WaitToReadSlowAsync` properly observes and propagates exceptions/cancellation from per-priority wait tasks instead of abandoning them when another lane completes, preventing `UnobservedTaskException` warnings and potential process termination.

## Scope
- `PrioritizedChannelReader.WaitToReadSlowAsync` in `src/Hugo/PrioritizedChannel.cs`.
- Requires restructuring the wait logic so every `WaitToReadAsync` call is awaited/observed, even when `Task.WhenAny` returns early.
- Can be implemented in tandem with allocation reduction (PA-011) but focus here is correctness.

## Requirements
1. After `Task.WhenAny` returns, ensure remaining waits are awaited or canceled so exceptions are observed (e.g., use `Task.WhenAll` on the outstanding tasks or avoid materializing multiple tasks).
2. Surface any observed exception via the reader (rethrow or wrap) so callers can handle it deterministically.
3. Maintain priority ordering and cancellation responsiveness.
4. Add documentation/changelog entry describing the bug fix.

## Deliverables
- Updated wait logic with deterministic exception observation.
- Unit tests that simulate a lane throwing an exception while another completes, verifying no unobserved exceptions occur.
- Documentation/changelog update.

## Acceptance Criteria
- Running scenarios where one priority lane faults no longer produces `UnobservedTaskException`.
- The reader propagates the first fault to callers (e.g., via `ReadAsync` throwing).
- Existing behavior for healthy lanes unchanged.

## References
- `src/Hugo/PrioritizedChannel.cs:273-337` – current implementation using `Task.WhenAny`.

## Testing Strategy
### Unit Test Scenarios
- `PrioritizedChannel_ShouldPropagateLaneException` – one lane throws; ensure reader surfaces exception.
- `PrioritizedChannel_ShouldObserveCancelledLane` – cancellation token cancels one lane mid-wait.

### Integration Test Scenarios
- Stress test with mixed priority producers that occasionally fail to ensure system remains stable.

### Feature Test Scenarios
- Not applicable externally; mention fix in docs.

## Risks & Mitigations
- **Risk:** Observing every task may add overhead. *Mitigation:* Combine with PA-011 optimizations to keep perf acceptable.

## Observability & Diagnostics
- Add logging or counters when a lane fault is propagated to aid troubleshooting.

## Rollout / Dependencies
- Coordinate release notes with downstream teams relying on PrioritizedChannel.
