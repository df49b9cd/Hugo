# PA-009 – Optimize Functional Failure Propagation

## Goal
Reduce redundant allocations and diagnostics when propagating failures through the `Functional` extension methods by adding a lightweight failure-cast path that reuses the original `Result`.

## Scope
- Applies to `Functional` extensions in `src/Hugo/Functional.cs` and potentially core `Result<T>` helpers.
- Introduce a `Result<T>.Cast<TOut>()` or similar helper that reuses the existing failure state without re-triggering `GoDiagnostics.RecordResultFailure()`.
- Update synchronous and asynchronous combinators (`Then`, `Map`, `SelectMany`, `Ensure`, etc.) to leverage the new helper when the source is already a failure.

## Requirements
1. Add an efficient failure-propagation helper (e.g., `Result<T>.CastFailure<TOut>()`) that copies the error without incrementing diagnostics again.
2. Refactor Functional combinators to call the helper whenever they need to propagate an existing failure.
3. Ensure success paths stay unchanged and diagnostics still count genuine failures.
4. Provide unit tests demonstrating that failure propagation no longer increments diagnostics multiple times and that struct allocations drop.
5. Document the behavior/performance improvements in changelog/dev guide.

## Deliverables
- New helper on `Result<T>` (or adjacent type) plus updated Functional combinators.
- Unit tests validating diagnostic counts and behavior.
- Benchmark or perf measurements (optional) showing reduced allocations/atomics.

## Acceptance Criteria
- Functional pipelines propagate failures without incrementing `GoDiagnostics.RecordResultFailure()` more than once per underlying failure.
- `Result<T>` allocation profile improves in microbenchmarks (e.g., compare before/after using BenchmarkDotNet).
- No regression in API behavior or exception safety.

## References
- `src/Hugo/Functional.cs` – combinator implementations.
- `src/Hugo/Result.cs` – `Result<T>` struct and diagnostics hooks.
- `src/Hugo/GoDiagnostics.cs` – diagnostic counters.

## Testing Strategy
### Unit Test Scenarios
- `Functional_Then_ShouldNotIncrementDiagnostics_OnFailurePropagation`.
- `Functional_SelectMany_ShouldReuseOriginalError`.
- Async counterparts ensuring cancellation/error paths still behave.

### Integration Test Scenarios
- Run representative pipelines (e.g., `Result.WhenAll` + `Functional` helpers) under failure load and assert diagnostics counts match actual failures.

### Feature Test Scenarios
- Update documentation to mention cast helper usage (if surfaced) and verify sample code.

## Risks & Mitigations
- **Risk:** Missing a combinator could leave inconsistent behavior. *Mitigation:* Audit all Functional methods and add tests.
- **Risk:** New helper could be misused by consumers for success cases. *Mitigation:* Keep it internal or clearly document intended use.

## Observability & Diagnostics
- After change, monitor diagnostic counters to ensure they align with failure counts in production telemetry.

## Rollout / Dependencies
- Communicate perf improvement to downstream teams so they can retest pipelines.
