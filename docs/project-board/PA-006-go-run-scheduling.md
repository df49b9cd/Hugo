# PA-006 – Provide Efficient Scheduling Paths For Go.Run And WaitGroup.Go

## Goal
Reduce unnecessary Task.Run overhead in `Go.Run` and `WaitGroup.Go` by offering scheduling options that avoid double-dispatch, minimize allocations, and honor caller-selected schedulers.

## Scope
- Applies to `src/Hugo/Go.cs` and `src/Hugo/Go/Go.WaitGroupExtensions.cs`.
- Introduce overloads or refactor existing ones to reuse already-created `Task`/`ValueTask` instances, allow custom `TaskScheduler`, and skip redundant closures when possible.
- Out of scope: broader changes to WaitGroup semantics or diagnostic counters.

## Requirements
1. Add overloads that accept an existing `Task`/`ValueTask` so callers can register work without re-wrapping it in `Task.Run`.
2. Expose optional `TaskCreationOptions`/`TaskScheduler` parameters (defaulting to `TaskScheduler.Default`) to give callers control over dispatch behavior.
3. Ensure existing APIs remain source-compatible by keeping current overloads forwarding to the new, configurable core implementation.
4. Update `WaitGroup.Go` to leverage the improved scheduling path, ensuring cancellation tokens are still respected.
5. Document the new overloads and guidance on choosing the appropriate scheduling strategy.

## Deliverables
- Refactored `Go.Run`/`WaitGroup.Go` implementations with configurable scheduling.
- Unit tests covering the new overloads and ensuring cancellation tokens still flow correctly.
- Documentation updates in Go helper docs and changelog entry.

## Acceptance Criteria
- Callers can enqueue already-started tasks without additional `Task.Run` allocations.
- Supplying a custom scheduler or creation options works as expected (validated via tests).
- Existing behavior remains unchanged when using the original overloads.
- No regressions in Go diagnostics or WaitGroup tests.

## References
- `src/Hugo/Go.cs:15-45` – current `Run` overloads.
- `src/Hugo/Go/Go.WaitGroupExtensions.cs` – WaitGroup helpers that currently call `Run`.
- `tests/Hugo.UnitTests/WaitGroupTests.cs` (if any) for regression coverage.

## Testing Strategy
### Unit Test Scenarios
- `GoRun_ShouldAcceptExistingTaskWithoutWrapping` – verify no additional Task.Run invocation occurs (use instrumentation).
- `WaitGroupGo_ShouldRespectCustomScheduler` – ensure provided scheduler runs work.
- `GoRun_WithCancellationToken_ShouldPropagateCancellation` – ensure tokens still cancel work.

### Integration Test Scenarios
- Use WaitGroup in a high-throughput scenario (e.g., channel pumps) to confirm reduced allocations and no behavioral regressions.

### Feature Test Scenarios
- Update documentation examples demonstrating both default and advanced scheduling paths; manually execute to verify behavior.

## Risks & Mitigations
- **Risk:** Introducing new overloads may confuse users. *Mitigation:* Provide clear docs and keep existing overloads simple.
- **Risk:** Misuse of custom schedulers could starve work. *Mitigation:* Default to `TaskScheduler.Default` and document guidance.

## Observability & Diagnostics
- Consider extending `GoDiagnostics` to record scheduling mode usage for telemetry.

## Rollout / Dependencies
- Coordinate with downstream repos (Odin, OmniRelay) before upgrading to ensure they adopt the new overloads gradually.
