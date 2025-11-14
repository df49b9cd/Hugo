# PA-007 – Run Compensation For Completed WhenAll Tasks On Cancellation

## Goal
Ensure `Result.WhenAll` executes compensation for operations that completed successfully before a cancellation request, preventing orphaned side effects when the orchestration stops early.

## Scope
- Applies to `WhenAllInternal` in `src/Hugo/Result.WhenAll.cs`.
- Requires capturing partial results when `Task.WhenAll` throws `OperationCanceledException` and folding their compensation scopes into the rollback step.
- Does not change `WhenAny` or policy execution helpers.

## Requirements
1. On `OperationCanceledException`, inspect the original task array and gather results from tasks with `IsCompletedSuccessfully`, absorbing their `Compensation` into the aggregate scope.
2. Optionally capture errors from tasks that faulted before cancellation for diagnostic purposes.
3. Invoke `RunCompensationAsync` with the populated scope so compensations run deterministically even under cancellation.
4. Preserve the current outward contract: return `Result.Fail<IReadOnlyList<T>>` with `Error.Canceled`.
5. Add clear documentation/changelog notes describing the deterministic rollback behavior.

## Deliverables
- Updated cancellation handling in `WhenAllInternal`.
- Unit tests proving compensation runs when cancellation occurs mid-flight (external token cancel and operation-thrown `OperationCanceledException`).
- Doc/changelog updates.

## Acceptance Criteria
- Cancellation after at least one operation succeeds triggers compensation before returning the canceled result.
- Cancellation before any operation completes leaves the compensation scope empty (no extra work).
- No regressions in success/failure handling; CI + Native-AOT builds stay green.

## References
- `src/Hugo/Result.WhenAll.cs:37-110` – target code.
- `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs` – current compensation coverage.

## Testing Strategy
### Unit Test Scenarios
- `WhenAll_ShouldRunCompensation_OnExternalCancellation`.
- `WhenAll_ShouldRunCompensation_OnOperationCanceledException`.
- `WhenAll_ShouldSkipCompensation_WhenNoTasksCompleted`.

### Integration Test Scenarios
- Execute `Result.TieredFallbackAsync` tiers with cancellation to ensure state rolls back.

### Feature Test Scenarios
- Update documentation snippet demonstrating cancellation + compensation; run it end-to-end.

## Risks & Mitigations
- **Risk:** Inspecting tasks post-cancel may race with completion. *Mitigation:* Use `IsCompletedSuccessfully`/`IsFaulted` guards.
- **Risk:** Additional loops add overhead. *Mitigation:* Work only occurs inside the cancellation path.

## Observability & Diagnostics
- Optionally log the number of compensations executed due to cancellation for operational insight.

## Rollout / Dependencies
- Notify downstream consumers before release so they rely on the new guarantees.
