# PA-002 – Execute Compensation For Partially Completed WhenAll Operations On Cancellation

## Goal
Guarantee that `Result.WhenAll` rolls back side effects when cancellation interrupts execution by running compensation for every operation that finished before the cancellation signal.

## Scope
- Limited to the cancellation handling path inside `WhenAllInternal` in `src/Hugo/Result.WhenAll.cs`.
- Requires capturing completed task results when `Task.WhenAll` throws and ensuring their compensation scopes are executed.
- No changes to `WhenAny` or `Result.ExecuteWithPolicyAsync` outside of what’s necessary to surface partial results.

## Requirements
1. When `Task.WhenAll(tasks)` throws `OperationCanceledException`, iterate the original `tasks` array: for each task with `IsCompletedSuccessfully`, retrieve its `PipelineOperationResult<T>` and absorb its `Compensation` into the aggregate scope.
2. For tasks faulted before cancellation, collect their errors just like the normal failure path to provide context in logs/diagnostics.
3. After collecting partial results, invoke `RunCompensationAsync` with the combined scope so registered compensations run even under cancellation.
4. Return a failed `Result<IReadOnlyList<T>>` with an `Error.Canceled` error (preserve existing outward contract) once compensation completes.
5. Ensure no duplicate compensation runs when the normal success path also clears scopes.

## Deliverables
- Updated implementation in `WhenAllInternal` handling partial results explicitly.
- Unit tests proving compensation executes on cancellation with both synchronous (`cts.Cancel()`) and asynchronous (`OperationCanceledException` inside an operation) triggers.
- Documentation/changelog noting the deterministic rollback behavior.

## Acceptance Criteria
- Cancellation occurs after some operations succeed and register compensation: their compensations execute before returning to the caller.
- If no operations completed, the compensation scope remains empty and no extra work happens.
- No regressions in existing `WhenAll` success/failure behavior or performance outside the cancellation path.
- All tests pass in CI.

## References
- `src/Hugo/Result.WhenAll.cs:37-110` – target logic.
- `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs:20-120` – compensation behavior under success/failure.

## Testing Strategy
### Unit Test Scenarios
- `WhenAll_ShouldRunCompensation_OnCancellationAfterSuccess`: one operation succeeds and registers compensation, cancellation fires; assert compensation executed.
- `WhenAll_ShouldIgnoreUncompletedTasks_OnCancellation`: cancellation before any task completes; ensure no compensation runs.
- `WhenAll_ShouldHandleOperationCanceledException_FromOperation`: operation throws `OperationCanceledException`; completed tasks still roll back.

### Integration Test Scenarios
- Use `Result.TieredFallbackAsync` tier exec with two strategies and cancel mid-run; confirm state restored.
- Simulate real-world pipeline (e.g., OmniRelay message dispatch) verifying partial effects revert on cancellation.

### Feature Test Scenarios
- Update documentation sample to mention compensation on cancellation and manually validate snippet behavior.

## Risks & Mitigations
- **Risk:** Inspecting tasks post-cancel could race with GC’d tasks. *Mitigation:* Keep references array alive and guard access with `IsCompleted` checks.
- **Risk:** Additional loops might affect latency. *Mitigation:* Work only executes inside cancellation catch, which is rare compared to success path.

## Observability & Diagnostics
- Optionally log the count of compensations executed due to cancellation for operational insight.

## Rollout / Dependencies
- Align with dependent repos before release so they expect compensation to run even when they cancel `WhenAll`.
