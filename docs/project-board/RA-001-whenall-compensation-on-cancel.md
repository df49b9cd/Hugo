# RA-001 – Run Compensation For Completed WhenAll Tasks During Cancellation

## Goal
Ensure `Result.WhenAll` executes compensation for every operation that completed before a cancellation request, even when `Task.WhenAll` throws `OperationCanceledException`, so external side effects are consistently rolled back.

## Scope
- Applies to `WhenAllInternal` in `src/Hugo/Result.WhenAll.cs` and supporting helpers.
- Requires capturing completed task results when `Task.WhenAll` is interrupted, and ensuring their compensation scopes participate in rollback.
- Does **not** change `WhenAny` semantics or `Result.ExecuteWithPolicyAsync`.

## Requirements
1. Replace the bare `Task.WhenAll(tasks)` call with logic that inspects each task’s status upon cancellation (e.g., iterate the original array and check `IsCompletedSuccessfully`).
2. For every task that finished successfully before cancellation, absorb its `Compensation` into the group scope to track registered rollback actions; for failures, collect structured errors as today.
3. Invoke `RunCompensationAsync` with the accumulated scope regardless of cancellation so completed operations are always rolled back.
4. Preserve the current outward contract: callers still receive `Result.Fail` with `ErrorCodes.Canceled`, but now side effects are cleaned up.
5. Cover both synchronous and asynchronous cancellation paths (external token and internal `throw` from operations).

## Deliverables
- Implementation updates to `WhenAllInternal` (and possibly a helper) to inspect partial results on cancellation.
- New/updated unit tests in `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs` or a dedicated suite to validate compensation runs when cancellation occurs mid-flight.
- Documentation/changelog notes summarizing the behavioral improvement.

## Acceptance Criteria
- When cancellation occurs after at least one operation succeeded and registered compensation, the compensation callback executes before `WhenAll` returns.
- No regression for the normal success/failure aggregation path.
- Cancellation still propagates an `Error.Canceled` result with the original token metadata.
- Tests pass locally and in CI.

## References
- `src/Hugo/Result.WhenAll.cs:60-110` – existing cancellation branch.
- `src/Hugo/Result.WhenAll.cs:246-320` – `ExecuteWithPolicyAsync` producing `PipelineOperationResult<T>`.
- `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs:38-115` – coverage for compensation.

## Testing Strategy
### Unit Test Scenarios
- Forced cancellation while one operation succeeds and registers compensation; assert compensation delegate runs and `Result.Fail` carries `ErrorCodes.Canceled`.
- Cancellation triggered by an operation throwing `OperationCanceledException`; ensure completed tasks still roll back.
- No-op compensation when no operations finished before cancellation.

### Integration Test Scenarios
- Use `Result.WhenAll` within `Result.TieredFallbackAsync` to verify tier compensation executes when the tier is canceled externally.
- End-to-end sample with two operations updating in-memory state and registering compensations; cancel mid-way and assert state restored.

### Feature Test Scenarios
- Update documentation sample (if any) to mention deterministic rollback on cancellation; manually validate snippet behaves as described.

## Risks & Mitigations
- **Risk:** Inspecting `Task` status after cancellation might race with continuations. *Mitigation:* Use `Task.Status`/`IsCompleted` checks and `await Task.WhenAll` only after filtering completed tasks.
- **Risk:** Additional loops could impact performance. *Mitigation:* Only perform extra work inside the cancellation path.

## Observability & Diagnostics
- Consider emitting diagnostics when compensation runs as part of cancellation to aid tracing.

## Rollout / Dependencies
- Coordinate with downstream consumers (Odin, OmniRelay) to pull the fix before relying on compensated cancellation semantics.
