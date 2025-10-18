# Hugo Copilot Instructions

## Library Purpose
- `Hugo` mirrors Go-style concurrency & result workflows for .NET 10; core sources live in `src/Hugo`.
- Bring helpers into scope with `using static Hugo.Go;` as shown throughout `tests/Hugo.Tests/GoTests.cs`.

## Result Pipeline
- `Result<T>` (see `src/Hugo/Result.cs`) is the currency of success/failure; build values with `Result.Ok/Fail` or the `Go.Ok/Err` shorthands.
- Extend pipelines via `src/Hugo/Functional.cs` (`Then`, `Map`, `Tap`, `Recover`, `Ensure`, `Finally`); prefer chaining over nested `if`/`try`.
- Exceptions should be wrapped with `Result.Try/TryAsync`; cancellations flow through `Error.Canceled` as enforced by async combinators.
- Example: `var result = Ok(input).Then(Validate).ThenAsync((v, ct) => ProcessAsync(v, ct));`

## Error Semantics
- Structured errors live in `Error` (`src/Hugo/Primitives.cs`); always populate codes from `ErrorCodes` (`error.validation`, `error.exception`, etc.).
- Add diagnostic context with `WithMetadata` just like `GoFunctionalTests.Err_ShouldWrapExceptionWithMetadata` expects an `exceptionType` entry.

## Concurrency Primitives
- `WaitGroup`, `Mutex`, `RwMutex`, `Pool`, and `Once` reside in `src/Hugo/Primitives.cs`; tests in `GoTests.cs` and `GoFunctionalTests.cs` cover expected behavior.
- Prefer `wg.Go(...)` (extension in `Go.cs`) to track background work and always finalize with `await wg.WaitAsync(TestContext.Current.CancellationToken);`.
- Locks return disposable releasers: `using (await mutex.LockAsync(ct)) { ... }` or `using (await rwMutex.RLockAsync(ct)) { ... }` ensures release even on exceptions.
- Channels come from `Go.MakeChannel<T>`; specify `capacity`/`BoundedChannelOptions` when you need bounded behavior (`GoFunctionalTests.MakeChannel_WithBoundedOptions_ShouldRespectSettings`).
- Use `Defer` for cleanup in the Go style (`using (Defer(() => ...))`), matching the pattern in file readers inside tests.

## Developer Workflow
- Ensure the .NET 10 preview SDK is installed; both projects target `net10.0`.
- Build the solution with `dotnet build Hugo.sln`.
- Run the xUnit v3 suite with `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj`; tests rely on `TestContext.Current.CancellationToken`, so avoid writing helpers that block without honoring cancellation tokens.
- When introducing new primitives, mirror the symmetry of sync/async APIs and back them with tests similar to `GoTests.RWMutex_ShouldProvide_ExclusiveWriteLock`.

## Style Expectations
- Public APIs guard against `null` arguments up-front and translate failures into `Result<T>`; follow that guard-first pattern.
- Async combinators accept `(value, CancellationToken, Task<Result<T>>)` signatures; keep the overload matrix consistent when adding new ones.
- Favor concise XML docs and minimal but high-value inline comments, matching existing source style.
