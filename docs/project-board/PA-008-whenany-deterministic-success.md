# PA-008 – Make WhenAny Return First Success Despite Concurrent Failures

## Goal
Guarantee that `Result.WhenAny` returns the first successful result even when other operations fail or cancel in the same scheduling slice, aligning runtime behavior with documentation.

## Scope
- Limited to `WhenAnyInternal` in `src/Hugo/Result.WhenAll.cs`.
- Requires gating error aggregation once a winner exists while preserving compensation execution for non-winning successes.
- Does not modify retry/compensation policies outside this method.

## Requirements
1. Track whether `winner` is set; after that point, ignore additional failures for the purpose of the returned result (optionally log them).
2. Continue aggregating errors when no winner exists to preserve current failure semantics.
3. Ensure linked cancellation still fires immediately when the winner is chosen.
4. Maintain compensation for secondary successes via `RunCompensationAsync`.
5. Document the behavior guarantee in XML docs/changelog.

## Deliverables
- Updated `WhenAnyInternal` implementation locking down post-winner failure handling.
- Unit tests covering success/failure race conditions.
- Documentation/changelog updates.

## Acceptance Criteria
- Scenario: one operation succeeds, another fails immediately after; `WhenAny` returns success and does not surface the failure.
- Scenario: winner plus a canceled operation; cancellation ignored as today.
- Scenario: no winner; aggregate failure/canceled result unchanged.
- All tests pass in CI; Native-AOT compatibility unaffected.

## References
- `src/Hugo/Result.WhenAll.cs:112-240` – existing behavior.
- `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs` – WhenAny coverage.

## Testing Strategy
### Unit Test Scenarios
- `WhenAny_ShouldReturnSuccess_WhenPeerFailsAfterWinner`.
- `WhenAny_ShouldIgnorePostWinnerCancellations`.
- `WhenAny_ShouldAggregateFailures_WhenNoWinner`.

### Integration Test Scenarios
- Exercise WhenAny inside fallback tiers or OmniRelay pipelines to ensure deterministic success surfaces.

### Feature Test Scenarios
- Update docs with a race example; run it to confirm behavior.

## Risks & Mitigations
- **Risk:** Masking failures could hide issues. *Mitigation:* Provide diagnostic hooks/logging for post-winner failures.

## Observability & Diagnostics
- Optionally increment a diagnostic counter when post-winner failures occur to aid debugging.

## Rollout / Dependencies
- Coordinate with downstream repos so they rely on the new deterministic success behavior.
