# EG-001 – Guard ErrGroup Against Post-Disposal Execution

## Goal
Ensure `ErrGroup` fails fast when callers attempt to register additional work after the group has been disposed, preventing background `ObjectDisposedException` surfacing as unrelated failures.

## Scope
- **In scope:** Updating `src/Hugo/Primitives/ErrGroup.cs` to track disposal state, reject late `Go(...)` calls, and provide actionable diagnostics.
- **Out of scope:** Changes to `WaitGroup`, other primitives, or policy execution semantics beyond disposal safety.

## Requirements
1. Introduce an `_disposed` flag guarded by `Volatile`/`Interlocked` or `IDisposable` pattern to detect teardown.
2. Every public `Go` overload checks disposal before queuing work and throws `ObjectDisposedException` referencing `ErrGroup`.
3. `RegisterAndRun` must no-op or throw when teardown raced with registration (e.g., `Add` succeeded but `_cts` already disposed).
4. The public `Token` property should continue to work after disposal for awaiting consumers; only new registrations are blocked.
5. Documentation comments mention that reusing an `ErrGroup` instance after disposal is unsupported and results in an exception.
6. Existing behavior for callers who never dispose remains unchanged.

## Deliverables
- Updated implementation of `ErrGroup` with disposal guards.
- XML doc comment update noting the error behavior.
- Unit tests proving exceptions are thrown deterministically.
- Changelog entry under the next release highlighting the breaking guard (if applicable).

## Acceptance Criteria
- Attempting to call any `Go(...)` overload after `Dispose` or a `using` scope completes immediately throws `ObjectDisposedException` before any background task runs.
- No regression in concurrent registration: multiple threads can still call `Go` concurrently prior to disposal without additional contention.
- `WaitAsync` and `Cancel()` remain callable after disposal for orderly shutdown.
- All new tests pass in CI, and existing suites remain green.

## References
- `src/Hugo/Primitives/ErrGroup.cs:32-150` – registration, overloads, and `RegisterAndRun`.
- `tests/Hugo.UnitTests/ErrGroupTests.cs` – existing coverage that should be extended.
- Async API audit note (`docs/async-api-audit.md#L340-L380`) for prior context.

## Testing Strategy
### Unit Test Scenarios
- Calling `Go(Func<Task>)` after disposing the group throws `ObjectDisposedException` synchronously.
- Concurrent disposal and registration: dispose on one thread while another calls `Go`; ensure only one exception is observed and no background task runs.
- `Token` is still cancellable/awaitable even after disposal (regression test).

### Integration Test Scenarios
- Embed `ErrGroup` inside a higher-level orchestration (e.g., `Result.TieredFallbackAsync`) and verify disposing the group while operations enqueue surfaces a deterministic exception rather than compensation errors.

### Feature Test Scenarios
- Documentation/snippet validation: run samples in `docs/reference/result-pipelines.md` to ensure `using var group = new ErrGroup();` continues to behave identically during normal usage.

## Risks & Mitigations
- **Risk:** Throwing earlier might break callers who relied on silent acceptance of post-disposal calls. *Mitigation:* Document the guard and review downstream usage; add feature flag only if necessary.
- **Risk:** Additional locking may reduce throughput. *Mitigation:* Use simple `Volatile.Read`/`Interlocked.Exchange` and avoid heavy locks.

## Observability & Diagnostics
- Consider logging via `GoDiagnostics` when `ObjectDisposedException` is thrown to aid adoption.

## Rollout / Dependencies
- Coordinate with any components (e.g., OmniRelay) known to reuse `ErrGroup` instances in pools to ensure they dispose at the proper boundary before adopting this change.
