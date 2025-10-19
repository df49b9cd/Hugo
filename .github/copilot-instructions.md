# Hugo Copilot Guide

## Purpose & Scope
- `Hugo` mirrors Go-style concurrency and Result workflows for .NET 10; the primary sources live in `src/Hugo`.
- Pull helpers into scope with `using static Hugo.Go;`, matching usage across `tests/Hugo.Tests/GoTests.cs`.
- Keep the library aligned with .NET parallel programming guidance; review Microsoft's parallel programming overview when extending primitives.

## Result Pipelines
- `Result<T>` (`src/Hugo/Result.cs`) is the unit of success or failure; build values with `Result.Ok/Fail` or the `Go.Ok/Err` shorthands.
- Prefer fluent composition via `src/Hugo/Functional.cs` helpers (`Then`, `Map`, `Tap`, `Recover`, `Ensure`, `Finally`) instead of branching logic.
- Wrap fallible work with `Result.Try/TryAsync`; cancellation should surface as `Error.Canceled` through the async combinators.
- Example:
```
var result = Ok(input)
    .Then(Validate)
    .ThenAsync((value, ct) => ProcessAsync(value, ct));
```

## Error Semantics
- Structured errors originate from `Error` (`src/Hugo/Primitives.cs`); always set an `ErrorCodes` value such as `error.validation` or `error.exception`.
- Attach context with `WithMetadata`, mirroring the expectation from `GoFunctionalTests.Err_ShouldWrapExceptionWithMetadata` for an `exceptionType` entry.

## Concurrency Primitives
- `WaitGroup`, `Mutex`, `RwMutex`, `Pool`, `Once`, and channel helpers live in `src/Hugo/Primitives.cs`; matching tests reside in `GoTests.cs` and `GoFunctionalTests.cs`.
- Use `wg.Go(...)` from `Go.cs` to track background work and finish with `await wg.WaitAsync(TestContext.Current.CancellationToken);`.
- Locks return disposable releasers: `using (await mutex.LockAsync(ct)) { ... }` and `using (await rwMutex.RLockAsync(ct)) { ... }` guarantee release even on exceptions.
- Build channels with `Go.MakeChannel<T>`; supply capacity or `BoundedChannelOptions` for bounded behavior as validated by `GoFunctionalTests.MakeChannel_WithBoundedOptions_ShouldRespectSettings`.
- Embrace Go-style cleanup with `using (Defer(() => ...))` to ensure deterministic teardown.
- Reuse the same synchronization primitive instance to guard a shared resource; mixing separate instances defeats the protection (mirrors .NET synchronization guidance).

## Developer Workflow
- Install the .NET 10 preview SDK; all projects target `net10.0`.
- Rely on these commands during routine development:
```
dotnet build Hugo.sln
dotnet test tests/Hugo.Tests/Hugo.Tests.csproj
```
- Tests depend on `TestContext.Current.CancellationToken`; avoid helpers that block without honoring cancellation.
- New primitives should keep sync/async parity and ship with tests similar to `GoTests.RWMutex_ShouldProvide_ExclusiveWriteLock`.

## Style Expectations
- Guard public APIs against `null` early and translate failures into `Result<T>` surfaces.
- Async combinators accept `(value, CancellationToken, Task<Result<T>>)` signatures; keep the overload matrix consistent when extending.
- Favor succinct XML docs and only add inline comments when they clarify non-obvious logic, aligning with the existing source style.
- Link internal guidance to external docs as needed; preferred references include Microsoft's parallel programming and managed threading basics articles.

## Reference Reading
- .NET Parallel Programming overview: https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/
- Managed threading basics: https://learn.microsoft.com/en-us/dotnet/standard/threading/managed-threading-basics
- Synchronization primitives overview: https://learn.microsoft.com/en-us/dotnet/standard/threading/overview-of-synchronization-primitives
