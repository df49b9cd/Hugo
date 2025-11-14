# PA-003 – Make WhenAny Return First Success Despite Concurrent Failures

## Goal
Ensure `Result.WhenAny` always returns the first successful result, even if other operations fail or cancel concurrently, aligning runtime behavior with documented expectations.

## Scope
- Changes confined to `WhenAnyInternal` in `src/Hugo/Result.WhenAll.cs`.
- Requires gating error aggregation once a winner is set, while preserving compensation execution for secondary successes.
- No modifications to retry or compensation policy implementations outside this method.

## Requirements
1. Track whether a `winner` has been established and suppress addition of new failure entries to `errors` once it is non-null.
2. Continue collecting errors when no winner has been found yet (as today) to handle the “all operations failed” path.
3. Ensure cancellation of the linked CTS still occurs immediately after selecting the winner.
4. Maintain compensation behavior for other successful operations by running `RunCompensationAsync` with their captured scopes even if some peers fail after the winner.
5. Document the guarantee in XML docs/changelog so callers know failures after the winner do not change the result.

## Deliverables
- Updated `WhenAnyInternal` logic implementing the new gating behavior.
- Unit tests covering the race condition where success and failure happen nearly simultaneously.
- Doc/changelog updates clarifying behavior.

## Acceptance Criteria
- Scenario: one operation succeeds, another fails shortly after; `WhenAny` returns the success result with no aggregate failure.
- Scenario: winner selected, another operation cancels; cancellation is ignored as before.
- Scenario: no operation succeeds; behavior unchanged (aggregate failure or canceled error).
- Tests pass in CI and Native-AOT compatibility is preserved.

## References
- `src/Hugo/Result.WhenAll.cs:112-241` – existing implementation.
- `tests/Hugo.UnitTests/ResultPipelineEnhancementsTests.cs:90-150` – coverage for `WhenAny`.

## Testing Strategy
### Unit Test Scenarios
- `WhenAny_ShouldReturnSuccess_WhenPeerFailsAfterWinner`: use `TaskCompletionSource` timing to trigger race.
- `WhenAny_ShouldIgnorePostWinnerCancellations`: ensure cancellation errors after winner do not propagate.
- `WhenAny_ShouldAggregateFailures_WhenNoWinner`: regression test to ensure failure path intact.

### Integration Test Scenarios
- Exercise `Result.WhenAny` inside fallback tiers to verify success surfaces even if other strategies fail concurrently.
- Run sample pipeline (e.g., OmniRelay) to confirm real workloads benefit from deterministic success.

### Feature Test Scenarios
- Update docs sample illustrating `WhenAny` usage; manually run to ensure output remains stable when injecting failures.

## Risks & Mitigations
- **Risk:** Masking failures could hide bugs. *Mitigation:* Add diagnostics/logging for post-winner failures without affecting result.
- **Risk:** Logic errors could skip necessary compensation. *Mitigation:* Keep compensation absorption/clearing unchanged and cover with tests.

## Observability & Diagnostics
- Optionally emit traces when post-winner failures occur for operators to inspect.

## Rollout / Dependencies
- Communicate change to dependent repositories so they can rely on deterministic success behavior.
