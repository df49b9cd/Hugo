# PA-005 – Run WhenAll Compensation For Completed Tasks During Cancellation

## Goal
Guarantee that `Result.WhenAll` executes compensation for operations that completed before a cancellation request, ensuring external side effects are rolled back even when the orchestration stops early.

## Scope
- Limited to the cancellation branch in `WhenAllInternal` within `src/Hugo/Result.WhenAll.cs`.
- Requires capturing completed task results when `Task.WhenAll` throws `OperationCanceledException` and running their compensation scopes.
- Does not modify `WhenAny` or `Result.ExecuteWithPolicyAsync` beyond what's necessary to expose partial results.

## Requirements
1. After `Task.WhenAll(tasks)` throws, iterate the original `tasks` array and gather results from tasks whose `Status == RanToCompletion`, absorbing their `Compensation` into the aggregate scope.
2. For tasks faulted prior to cancellation, collect their errors for diagnostic purposes (optional) but prioritize compensation for completed successes.
3. Invoke `RunCompensationAsync` with the captured scope regardless of cancellation so registered compensations are executed deterministically.
4. Preserve the outward API contract: callers still receive `Result.Fail<IReadOnlyList<T>>` with `Error.Canceled`.
5. Avoid double-running compensation on the normal success path.

## Deliverables
- Updated `WhenAllInternal` implementation handling partial results on cancellation.
- New unit tests verifying compensation executes when cancellation happens mid-flight (both external token cancel and operation-thrown `OperationCanceledException`).
- Documentation/changelog notes describing the improved behavior.

## Acceptance Criteria
- Scenario: two operations, first succeeds and registers compensation, cancellation occurs before second finishes. Compensation executes before returning the canceled result.
- Scenario: cancellation happens before any operation completes; no compensation runs.
- No regressions in success/failure aggregation or performance outside cancellation.
- CI + Native-AOT builds remain green.

## References
- `src/Hugo/Result.WhenAll.cs:37-110` – target code.
- `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs` – existing compensation coverage.

## Testing Strategy
### Unit Test Scenarios
- `WhenAll_ShouldRunCompensation_OnExternalCancellation`.
- `WhenAll_ShouldRunCompensation_OnOperationCanceledException`.
- `WhenAll_ShouldSkipCompensation_WhenNoTasksCompleted`.

### Integration Test Scenarios
- Use `Result.TieredFallbackAsync` tier execution or OmniRelay pipelines to ensure partial work is rolled back when canceling a tier mid-run.

### Feature Test Scenarios
- Update doc samples or snippets demonstrating cancellation + compensation; execute them to confirm behavior.

## Risks & Mitigations
- **Risk:** Inspecting `Task` results after cancel could race with completion. *Mitigation:* Keep references alive and check `IsCompletedSuccessfully` before reading `.Result`.
- **Risk:** Added loops may add overhead. *Mitigation:* Cancellation path is rare; cost acceptable.

## Observability & Diagnostics
- Optionally emit diagnostics counting compensations executed during cancellation.

## Rollout / Dependencies
- Coordinate with downstream repos so they expect compensation to run during cancellation before releasing.
