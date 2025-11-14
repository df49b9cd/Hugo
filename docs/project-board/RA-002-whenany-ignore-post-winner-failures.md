# RA-002 – Ensure WhenAny Returns First Success Even When Peers Fail Concurrently

## Goal
Prevent `Result.WhenAny` from turning a successful completion into a failure simply because another operation faulted during the same scheduling slice, aligning behavior with the documented “return the first successful result” contract.

## Scope
- Applies solely to `WhenAnyInternal` in `src/Hugo/Result.WhenAll.cs`.
- Requires refining error aggregation so failures observed after a winner is chosen no longer poison the overall result.
- Does not change compensation semantics for secondary successes or the “all operations failed” path.

## Requirements
1. Track the `winner` state and short-circuit error collection once a winner is selected; only failures that occurred before cancellation should influence the final result when `winner != null`.
2. Continue aggregating errors when no winner exists, preserving the existing `Error.Aggregate("All operations failed", …)` behavior.
3. Document that post-winner failures are treated as diagnostics (optional logging) but do not change the returned result.
4. Verify that compensation still runs for secondary successes, even if failures happen concurrently.
5. Maintain deterministic behavior regardless of task completion order.

## Deliverables
- Code changes to `WhenAnyInternal` that gate error accumulation on `winner == null` (or similar logic).
- Unit tests capturing the race where one task succeeds and another fails milliseconds later; ensure the method returns success.
- Documentation/changelog mention clarifying the behavior.

## Acceptance Criteria
- In scenarios where at least one operation succeeds, `WhenAny` always returns that success even if other operations fail after the winner is chosen.
- If no operation succeeds, behavior is unchanged (aggregate failure or canceled error as before).
- Compensation for non-winning successes still runs.
- All tests pass in CI.

## References
- `src/Hugo/Result.WhenAll.cs:112-241` – existing `WhenAnyInternal` implementation.
- `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs:70-145` – coverage for `WhenAny` success/failure cases.

## Testing Strategy
### Unit Test Scenarios
- Two operations: one succeeds quickly, one fails after a small delay; expect success result and no aggregated failure.
- Winner plus a canceled operation; ensure cancel is ignored as today.
- No winner case remains unchanged.

### Integration Test Scenarios
- Embed `WhenAny` within a higher-level orchestrator (e.g., fallback tiers) to confirm successes bubble through despite concurrent failures.

### Feature Test Scenarios
- Update docs/examples to highlight the guarantee and manually run the snippet verifying success is returned.

## Risks & Mitigations
- **Risk:** Hiding failures might mask real issues. *Mitigation:* Optionally log secondary failures or expose them via diagnostics without changing API behavior.

## Observability & Diagnostics
- Consider emitting a trace when a failure is observed after the winner to aid debugging without bubbling it to the caller.

## Rollout / Dependencies
- Inform dependent repos (Odin, OmniRelay) so they rely on the documented deterministic behavior.
