# Hugo.UnitTests – Unit Testing Guide

This document explains the scope, goals, and day-to-day practices for `tests/Hugo.UnitTests`. Use it as the reference for adding or updating low-level tests in Hugo.

## Scope

- Guard the public contracts in `src/Hugo` and `benchmarks/Hugo.Profiling` that can be validated in isolation: `Result`/`Optional`, error materialization, deterministic records/stores, synchronization primitives, and profiling analyzers.
- Keep coverage focused on pure or in-memory components (no durable stores, background pumps, or multi-service orchestration).
- Defer boundary, queue, workflow, or Go-style concurrency scenarios to `tests/Hugo.IntegrationTests` and `tests/Hugo.FeatureTests`.

## Goals

- Catch regressions in functional primitives before they cascade into higher-level workflows.
- Provide executable examples that show the idiomatic way to compose `Result`, `Optional`, error metadata, and profiling helpers.
- Maintain a fast feedback loop so contributors can run the entire suite locally on every SDK we target (`net9.0`, `net10.0`).

## Reasoning

Hugo leans on deterministic primitives. Small mistakes in `Result` composition, metadata serialization, or in-memory store semantics tend to surface far from the bug. Encoding those invariants here—where everything is synchronous, fakeable, and cheap—prevents repeated firefighting in integration and feature tests.

## Methodology & Approach

### Principles

1. **Test contracts, not plumbing.** Checks focus on observable behavior such as error codes, serialized payloads, replay counts, or analyzer output.
2. **Stay deterministic.** Favor in-memory stores, local builders, and stub clocks so each test can run in isolation without waiting for wall time.
3. **Explain failure modes.** Every test should document what happens when a contract is violated (which error code, which exception, or which value is returned).

### Test Taxonomy

| Area | Files | Notes |
| ---- | ----- | ----- |
| Result/Optional primitives | `ResultTests.cs`, `ResultEdgeCaseTests.cs`, `OptionalTests.cs`, `ResultPropertyTests.cs` | Validate success/failure propagation, algebraic laws, and async helpers.
| Result pipelines & sagas | `FunctionalTests.cs`, `ResultFallbackTests.cs`, `ResultPipelineEnhancementsTests.cs` | Cover combinators (`Tap`, `Then`, `SagaBuilder`) and recovery helpers.
| Error & diagnostics | `ErrorTests.cs`, `ErrorJsonConverterTests.cs`, `ErrGroupTests.cs` | Ensure metadata, serialization, and grouping stay backward compatible.
| Deterministic storage | `DeterministicRecordTests.cs`, `InMemoryDeterministicStateStoreTests.cs`, `DeterministicJsonContextTests.cs` | Keep replay counts, version stamps, and JSON contexts stable.
| Synchronization primitives | `Primitives/SynchronizationPrimitivesTests.cs` | Verify wait groups, latches, and other helpers behave deterministically.
| Profiling analyzers | `Profiling/CounterAnalyzerTests.cs`, `Profiling/SpeedscopeAnalyzerTests.cs` | Check CSV and Speedscope ingestion logic stays resilient.

### Tooling

- **xUnit.net v3** for the core testing framework.
- **FsCheck** for property-based coverage (see `ResultPropertyTests`).
- **Microsoft.Extensions.TimeProvider.Testing** even in unit tests when fake clocks simplify async verification.
- **coverlet.collector** for CI-friendly coverage reports via `dotnet test --collect:"XPlat Code Coverage"`.

## Setup

1. Install the latest .NET 10 SDK.
2. Restore the solution: `dotnet restore Hugo.slnx`.
3. (Optional) set `DOTNET_ROLL_FORWARD=Major` if your environment blocks preview TFMs.

## Execution

Run the full suite across both target frameworks:

```bash
# All target frameworks
dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj

# Focus on a single target for quicker feedback
dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj -f net9.0
```

Common filters:

```bash
# Result-specific tests
dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj --filter "FullyQualifiedName~ResultTests"

# Property-based tests only
dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj --filter "Category=Property"
```

Coverage and logging:

```bash
dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=lcov \
  /p:CoverletOutput=./TestResults/coverage

dotnet test tests/Hugo.UnitTests/Hugo.UnitTests.csproj --logger "trx;LogFileName=TestResults/hugo-unit-tests.trx"
```

## Best Practices

- **Prefer fakes over real delays.** Use `FakeTimeProvider` or inline schedulers instead of `Task.Delay`.
- **Isolate dependencies.** Stick to `InMemoryDeterministicStateStore`, `ResultSagaBuilder`, or local builders rather than external providers.
- **Intention-revealing names.** Keep the `Method_Condition_ShouldResult` format for new facts.
- **Lean on property tests for algebraic laws.** Anything mapping or binding `Result`/`Optional` should have an FsCheck counterpart.
- **Check specific error codes.** Compare against `ErrorCodes` values instead of boolean flags to clarify failure modes.

## Scenarios & Examples

### 1. Core Result semantics (`tests/Hugo.UnitTests/ResultTests.cs`)

```csharp
[Fact]
public void Fail_WithNullError_ShouldReturnUnspecified()
{
    var result = Result.Fail<int>(null!);

    result.IsFailure.ShouldBeTrue();
    result.Error?.Code.ShouldBe(ErrorCodes.Unspecified);
}
```

### 2. Deterministic state storage (`tests/Hugo.UnitTests/InMemoryDeterministicStateStoreTests.cs`)

```csharp
[Fact]
public void TryGet_ShouldReturnExistingRecord()
{
    var store = new InMemoryDeterministicStateStore();
    var record = new DeterministicRecord("hugo.test", 2, [1], DateTimeOffset.UtcNow);

    store.Set("key", record);

    store.TryGet("key", out var retrieved).ShouldBeTrue();
    retrieved.ShouldBeSameAs(record);
}
```

### 3. Property-based guarantees (`tests/Hugo.UnitTests/ResultPropertyTests.cs`)

```csharp
Check.QuickThrowOnFailure(
    Prop.ForAll<int>(static value =>
    {
        var input = Result.Ok(value);
        var projected = input.Map(static v => v * 2).Map(static v => v + 1);
        var composed = input.Map(static v => (v * 2) + 1);

        return projected == composed;
    }));
```

### 4. Profiling analyzers (`tests/Hugo.UnitTests/Profiling/CounterAnalyzerTests.cs`)

```csharp
[Fact]
public void Analyze_ComputesSummariesAndFindings()
{
    const string csv = "Timestamp,Provider,Counter Name,Counter Type,Mean/Increment\n" +
        "00:00:01,worker,Queue Depth,Mean,42";

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
    var report = CounterAnalyzer.Analyze(stream);

    report.TotalRows.ShouldBe(1);
    report.Counters.ShouldHaveSingleItem();
    report.Counters[0].Name.ShouldBe("Queue Depth");
}
```

## Continuous Improvement

- Add or update tests in this project whenever you touch the primitives it covers.
- Pin bugs with a failing unit test before fixing the implementation.
- Keep this README current when tooling, patterns, or scope changes so the next contributor can follow the same playbook.
