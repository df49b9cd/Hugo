# PA-004 – Harden ErrGroup Cancellation Semantics

## Goal
Ensure `ErrGroup` reliably surfaces cancellation as a failure and cancels peer operations immediately, preventing “success” results after manual aborts and reducing wasted work.

## Scope
- Applies only to `src/Hugo/Primitives/ErrGroup.cs`.
- Covers both the public `Cancel()` API and the policy-based pipeline overload (`ExecutePipelineAsync`).
- Out of scope: changes to `WaitGroup`, other primitives, or downstream consumers beyond doc updates.

## Requirements
1. Modify `Cancel()` so it records `Error.Canceled` via `TrySetError` before calling `_cts.Cancel()` whenever `_error` is still null.
2. Update `ExecutePipelineAsync` to capture failures, set `_error`, and signal `_cts` before invoking `Result.RunCompensationAsync`, ensuring other `Go` operations see cancellation immediately.
3. Preserve existing error aggregation when compensation fails (aggregate original + compensation errors).
4. Extend XML documentation/comments to state that manual cancellation yields a failed `Result<Unit>`.
5. Maintain existing behavior for non-pipeline `Go` overloads.

## Deliverables
- Updated `ErrGroup` implementation reflecting the new ordering.
- Enhanced unit tests in `tests/Hugo.UnitTests/ErrGroupTests.cs` covering manual cancellation before/after work, plus peer cancellation timing.
- Documentation and changelog entries describing the behavioral change.

## Acceptance Criteria
- `Cancel()` followed by `WaitAsync` returns `Result.Fail<Unit>` with `ErrorCodes.Canceled`, and `group.Error` matches.
- When a pipeline step fails, another long-running step observing `Token` is cancelled before compensation completes (validated via test instrumentation).
- No regressions in existing ErrGroup tests; Native-AOT compatibility unaffected.

## References
- `src/Hugo/Primitives/ErrGroup.cs:119-213` – `WaitAsync`, `Cancel`, `ExecutePipelineAsync`.
- `tests/Hugo.UnitTests/ErrGroupTests.cs` – current coverage.
- Docs: `docs/reference/result-pipelines.md` (ErrGroup integration section).

## Testing Strategy
### Unit Test Scenarios
- `Cancel_ShouldSurfaceCanceledResult_WhenNoWorkStarted`.
- `Cancel_ShouldNotOverrideExistingError` (failure then cancel).
- `PipelineFailure_ShouldCancelPeersBeforeCompensation` using `TaskCompletionSource` to assert ordering.

### Integration Test Scenarios
- Drive ErrGroup via `Result.TieredFallbackAsync` to confirm cancellation marks the overall pipeline as failed and stops strategies promptly.
- Run OmniRelay/other consumer scenario to ensure long-running transports stop on first failure.

### Feature Test Scenarios
- Update doc snippet showing cancellation handling and execute it end-to-end.

## Risks & Mitigations
- **Risk:** Behavior change may surprise callers relying on success-after-cancel. *Mitigation:* Highlight in release notes and provide migration tips.
- **Risk:** Reordering might expose race conditions. *Mitigation:* Thorough tests and code review.

## Observability & Diagnostics
- Optionally add tracing/logging when cancellation is recorded to aid debugging.

## Rollout / Dependencies
- Coordinate rollout with downstream repos (Odin, OmniRelay) before release.
