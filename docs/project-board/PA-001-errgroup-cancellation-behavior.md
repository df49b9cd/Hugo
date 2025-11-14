# PA-001 – Fix ErrGroup Cancellation Reporting and Peer Shutdown Ordering

## Goal
Ensure `ErrGroup` consistently reports cancellation as a failure and cancels peer operations immediately upon the first error, matching the library’s guarantees for deterministic teardown.

## Scope
- Applies to `src/Hugo/Primitives/ErrGroup.cs` only.
- Includes recording a structured cancellation error whenever `Cancel()` is invoked without a prior failure, and reordering `ExecutePipelineAsync` so peer operations are cancelled before long-running compensation completes.
- Out of scope: changes to `WaitGroup` or external consumers other than doc updates.

## Requirements
1. Update `Cancel()` to call `TrySetError(Error.Canceled(...))` when `_error` is null before signaling `_cts`, so `WaitAsync` surfaces `ErrorCodes.Canceled`.
2. Modify `ExecutePipelineAsync` to set the failure (`TrySetError`) and signal `_cts` before awaiting `Result.RunCompensationAsync`, ensuring other `Go` operations observe cancellation immediately.
3. Preserve existing structured error aggregation, including compensation failures via `Error.Aggregate`.
4. Add XML documentation clarifying that manual cancellation results in a failed `Result<Unit>`.
5. Existing non-pipeline `Go` overloads must continue to propagate exceptions and results unchanged.

## Deliverables
- Updated `ErrGroup` implementation reflecting the new cancellation semantics.
- Unit tests covering manual cancellation before/after work starts and verifying `_cts.Token` cancellation order.
- Documentation/changelog entries describing the behavior change.

## Acceptance Criteria
- Calling `Cancel()` before registering work causes `WaitAsync` to return `Result.Fail<Unit>` with `ErrorCodes.Canceled`, and `Error` is set.
- When a pipeline step fails, peer tasks observe cancellation before compensation completes (validated via synchronization primitives in tests).
- No regressions in existing `ErrGroupTests` and related suites.
- Native-AOT compatibility remains intact (no reflection/dynamic code added).

## References
- `src/Hugo/Primitives/ErrGroup.cs:119-213` – `WaitAsync`, `Cancel()`, and `ExecutePipelineAsync`.
- `tests/Hugo.UnitTests/ErrGroupTests.cs:73-210` – current cancellation coverage.
- Docs: `docs/reference/result-pipelines.md` (ErrGroup integration section).

## Testing Strategy
### Unit Test Scenarios
- `Cancel_ShouldReturnCanceledResult_WhenCalledBeforeWork`: verify `WaitAsync` failure and stored `Error`.
- `Cancel_ShouldNotOverwriteExistingFailure`: start failing task then call `Cancel()`; ensure original error persists.
- `ExecutePipelineAsync_ShouldCancelPeersBeforeCompensation`: use `TaskCompletionSource` to assert cancellation is observed before compensation completes.

### Integration Test Scenarios
- Drive `ErrGroup` via `Result.TieredFallbackAsync` to confirm tier cancellation now surfaces failure and stops strategies promptly.
- Run OmniRelay transport pump scenario (if available) to confirm worker tasks exit immediately when a peer fails.

### Feature Test Scenarios
- Update ErrGroup example in docs to demonstrate handling canceled results; execute snippet end-to-end.

## Risks & Mitigations
- **Risk:** Consumers relying on “success after Cancel” may break. *Mitigation:* Highlight behavior change in release notes and provide guidance.
- **Risk:** Reordering cancellation/compensation could expose race conditions. *Mitigation:* Comprehensive tests and code review.

## Observability & Diagnostics
- Optionally add tracing/logging hooks when cancellation is recorded to aid debugging.

## Rollout / Dependencies
- Coordinate with downstream repos (Odin, OmniRelay) to adopt the fix before releasing.
