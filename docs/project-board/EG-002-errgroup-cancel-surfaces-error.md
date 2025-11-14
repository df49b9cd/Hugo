# EG-002 – Surface Cancellation Error When ErrGroup Is Manually Canceled

## Goal
Make manual calls to `ErrGroup.Cancel()` propagate a structured cancellation error so `WaitAsync` callers can distinguish between “all work completed” and “group aborted”.

## Scope
- Update `ErrGroup.Cancel()` and supporting plumbing to record a cancellation error when none exists.
- Ensure callers of `WaitAsync` observe a failed `Result<Unit>` with `ErrorCodes.Canceled` whenever cancellation originates inside the group.
- No changes to linked external tokens or cancellation semantics in other APIs.

## Requirements
1. `Cancel()` must call `TrySetError(Error.Canceled(...))` with a descriptive message before signaling `_cts`, but only when `_error` is still `null`.
2. If an error already exists (e.g., a prior failure), `Cancel()` should preserve it and simply propagate the token.
3. `WaitAsync` should continue converting `OperationCanceledException` thrown by `_waitGroup` into `Error.Canceled`, but now the stored `_error` must match.
4. Update XML documentation to state that cancellation causes `WaitAsync` to resolve as a failure.
5. Existing public API shape must remain source/binary compatible.

## Deliverables
- Implementation update to `ErrGroup.Cancel()` and any supporting tests/helpers.
- Expanded tests covering manual cancellation flows in `tests/Hugo.UnitTests/ErrGroupTests.cs`.
- Release notes entry calling out the clarified cancellation contract.

## Acceptance Criteria
- Calling `Cancel()` before any work starts results in `WaitAsync` returning `Result.Fail<Unit>(ErrorCodes.Canceled)` and `group.Error` referencing the same error.
- Calling `Cancel()` after the first failure does not overwrite that failure.
- `Result.TieredFallbackAsync` and other APIs using `ErrGroup` continue to interpret cancellation correctly (validated via integration tests).
- All new tests pass, and there are no regressions in stress scenarios.

## References
- `src/Hugo/Primitives/ErrGroup.cs:119-150` (`WaitAsync`) and `:140-147` (`Cancel`).
- `tests/Hugo.UnitTests/ErrGroupTests.cs:117-168` – existing cancellation tests.
- `src/Hugo/Result.Fallbacks.cs:187-266` – integration usage of cancellation tokens.

## Testing Strategy
### Unit Test Scenarios
- `Cancel_ShouldReturnCanceledResultWhenNoWorkStarted`: verify `WaitAsync` surfaces the stored error and `Token.IsCancellationRequested` is true.
- `Cancel_ShouldNotOverwriteExistingError`: start two tasks, make one fail, then call `Cancel()`; ensure the original error is preserved.
- `WaitAsync_ShouldReturnCanceledWhenCancelCalledDuringInFlight`: register a long-running task, call `Cancel()`, and ensure `WaitAsync` returns canceled without relying on external `CancellationTokenSource`.

### Integration Test Scenarios
- Exercise `Result.TieredFallbackAsync` tier cancellation path to ensure decorated errors still include tier metadata when the group triggers cancellation.
- Use OmniRelay or sample hosting pipeline to confirm metrics/logs track cancellation as a failure event.

### Feature Test Scenarios
- Documentation snippet: update `docs/reference/result-pipelines.md` example to explicitly show handling of a canceled result; run the snippet to validate behavior.

## Risks & Mitigations
- **Risk:** Some callers may have interpreted a successful `WaitAsync` after manual cancellation as “everything is fine.” *Mitigation:* highlight the behavioral change in release notes and provide migration guidance.
- **Risk:** Double-setting errors could introduce races. *Mitigation:* continue using `Interlocked.CompareExchange` inside `TrySetError` and add regression tests.

## Observability & Diagnostics
- Update any dashboards or alerts that counted “successful” ErrGroup completions to instead consider cancellation as a separate category.

## Rollout / Dependencies
- Coordinate with maintainers of `ResultFallbackTests` and OmniRelay repositories to pull in the updated behavior before tagging a release.
