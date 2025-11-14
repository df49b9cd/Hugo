# EG-003 – Cancel ErrGroup Peers Before Long-Running Compensation

## Goal
Ensure policy-backed `ErrGroup.Go` overloads cancel peer operations as soon as a pipeline step fails, rather than waiting for compensation handlers to finish, so remaining work stops promptly and latency improves under failure.

## Scope
- Applies only to the overload that accepts `(ResultPipelineStepContext, CancellationToken, ValueTask<Result<Unit>>)` along with a `ResultExecutionPolicy`.
- Requires restructuring error-handling flow so `_cts.Cancel()` fires immediately once failure is detected.
- Compensation execution must still run to completion, but it should no longer block peer cancellation.
- No changes to `Result.ExecuteWithPolicyAsync` itself.

## Requirements
1. Split the logic inside `ExecutePipelineAsync` so the first failure captures the error, cancels the group, and schedules compensation afterward (e.g., via `Task.Run` or awaiting after cancellation is signaled).
2. Ensure cancellation tokens passed to other `Go` operations observe `_cts` as soon as the error occurs.
3. Preserve compensation semantics (sequential reverse, etc.) and continue aggregating compensation errors via `Error.Aggregate`.
4. Document the ordering guarantee: cancellation notification happens before compensation completes.
5. Maintain structured error information, including metadata from compensation failures.
6. Provide configuration hooks/tests to guarantee no duplicate compensation runs when multiple failures surface simultaneously.

## Deliverables
- Refactored `ExecutePipelineAsync` in `ErrGroup` to decouple cancellation from compensation duration.
- Additional helper(s) if needed (e.g., `TrySetErrorAndCancelAsync`).
- Unit and integration tests validating new ordering behavior.
- Documentation updates in `docs/reference/result-pipelines.md` and changelog entry.

## Acceptance Criteria
- When a pipeline step returns failure, peer tasks receive cancellation within the same scheduling turn (before compensation begins to run to completion).
- If compensation later fails, the aggregate error still includes both the original failure and the compensation failure.
- No regression in scenarios where compensation is fast; success path remains unchanged.
- CI passes with new coverage, and perf regression tests (if any) show neutral or improved latency under failure.

## References
- `src/Hugo/Primitives/ErrGroup.cs:179-212` – current `ExecutePipelineAsync` implementation.
- `src/Hugo/Result.WhenAll.cs:240-320` – reference compensation orchestration logic.
- `tests/Hugo.UnitTests/ResultFallbackTests.cs:147-208` – coverage invoking pipeline-aware `Go`.

## Testing Strategy
### Unit Test Scenarios
- `ExecutePipelineAsync_ShouldCancelPeersBeforeCompensation`: instrument operations with `TaskCompletionSource` to confirm cancellation is observed before compensation finishes.
- `ExecutePipelineAsync_ShouldAggregateCompensationErrorAfterCancel`: simulate compensation failure; assert final error message matches existing format.
- `ExecutePipelineAsync_ShouldNoOpWhenMultipleFailuresRace`: ensure `TrySetError` still gates cancellation and compensation runs exactly once.

### Integration Test Scenarios
- Use `Result.TieredFallbackAsync` with mixed fast/slow operations to verify the group’s token is signaled immediately upon first failure even when compensation takes hundreds of milliseconds.
- Run OmniRelay channel pump scenario (if available) to confirm long-running transports stop quickly when one pipeline step fails.

### Feature Test Scenarios
- Update sample in `docs/reference/result-pipelines.md` demonstrating retry + compensation: add logging timestamps to show cancellation occurs before compensation logs finish.
- Execute the documentation snippet end-to-end.

## Risks & Mitigations
- **Risk:** Cancelling before compensation could leave shared resources in limbo if compensation assumed peers kept running. *Mitigation:* Document ordering change and audit compensation callbacks for dependencies.
- **Risk:** Running compensation concurrently with peer cancellation might introduce race conditions. *Mitigation:* Keep compensation sequential but ensure it executes after cancellation notification via awaited continuation.

## Observability & Diagnostics
- Add trace/event instrumentation (optional) around “pipeline-step-failed”, “group-canceled”, and “compensation-completed” to aid troubleshooting.

## Rollout / Dependencies
- Coordinate rollout with teams relying on the older ordering semantics (docs mentions OmniRelay & Odin). Provide upgrade notes and timeline.
