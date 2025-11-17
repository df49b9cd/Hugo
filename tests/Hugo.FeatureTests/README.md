# Hugo.FeatureTests – Feature Testing Guide

Feature tests exercise Hugo’s Go-style orchestration end-to-end: channels, timers, wait groups, and execution policies running together under deterministic conditions. Use this guide whenever you add or modify tests in `tests/Hugo.FeatureTests`.

## Scope

- Validate multi-component scenarios that combine primitives from `Hugo.Go`, task queues, and execution policies.
- Cover higher-level behaviors such as channel fan-out, select builders, fake timers, prioritised channel routing, and cancellable workflows.
- Exclude deep system integration (covered by `tests/Hugo.IntegrationTests`) and low-level contracts (covered by `tests/Hugo.UnitTests`).

## Goals

- Prove that the developer-facing API surface behaves correctly under realistic orchestration patterns.
- Demonstrate idiomatic usage for channels, timers, wait groups, and cancellation so contributors can model new workflows.
- Keep the suite deterministic and fast despite exercising concurrency-heavy code.

## Reasoning

Hugo’s Go-like helpers shine only when you stitch them together. Bugs often emerge from coordination between multiple channels or from timing glitches in execution policies. Feature tests run those “real” orchestrations in a controlled lab so we can iterate quickly without spinning up infrastructure or waiting on wall clock time.

## Methodology & Approach

### Principles

1. **Model user workflows.** Each spec should read like an example from the docs—channels, timers, wait groups, and policies working together.
2. **Stay deterministic.** Drive time with `FakeTimeProvider` and use in-memory channels so every run is repeatable.
3. **Validate business outcomes.** Check end results (`ErrorCodes.Timeout`, aggregated values, prioritised ordering) rather than internal state.

### Test Taxonomy

| Area | Files | Notes |
| ---- | ----- | ----- |
| Channel builders & helpers | `Go/Channels/ChannelBuilderTests.cs`, `Go/Channels/GoChannelHelpersTests.cs`, `Go/Channels/GoTests.Channels.cs` | Validate bounded/unbounded builders, prioritized routing, and helper semantics. |
| General orchestration | `Go/General/GoFunctionalTests.cs`, `Go/General/GoTests.General.cs`, `Go/General/GoWaitGroupExtensionsTests.cs` | Cover end-to-end workflows, wait groups, fan-out/fan-in, and select builders. |
| Execution policies | `Go/ExecutionPolicies/GoTests.ExecutionPolicies.cs`, `Go/ExecutionPolicies/GoExecutionHelpersTests.cs` | Ensure timeout/race policies propagate success, cancellation, and failures correctly. |
| Timers & scheduling | `Go/Time/TimerTests.cs` | Exercise `After`, tickers, and cancellation-friendly timers via virtual time. |

### Tooling

- **xUnit.net v3** for the test runner and `[Collection]` control (see `GoTestsCollection.cs` for serialized suites).
- **Microsoft.Extensions.TimeProvider.Testing** to fast-forward workflows without sleeping.
- **FsCheck** is available through the common test package set; use it when scenarios need randomized coverage.
- **coverlet.collector** for CI-friendly coverage reports.

## Setup

1. Install the latest .NET 10 preview SDK.
2. Restore the workspace: `dotnet restore Hugo.slnx`.
3. Ensure preview TFMs are allowed (set `DOTNET_ROLL_FORWARD=Major` if needed).

## Execution

Run the full feature suite:

```bash
dotnet test tests/Hugo.FeatureTests/Hugo.FeatureTests.csproj
```

Limit to a single TFM for faster feedback:

```bash
dotnet test tests/Hugo.FeatureTests/Hugo.FeatureTests.csproj -f net9.0
```

Filter by area:

```bash
dotnet test tests/Hugo.FeatureTests/Hugo.FeatureTests.csproj --filter "FullyQualifiedName~GoTests.Channels"
dotnet test tests/Hugo.FeatureTests/Hugo.FeatureTests.csproj --filter "GoFunctionalTests"
```

Collect coverage or diagnostics:

```bash
dotnet test tests/Hugo.FeatureTests/Hugo.FeatureTests.csproj \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=lcov \
  /p:CoverletOutput=./TestResults/coverage

dotnet test tests/Hugo.FeatureTests/Hugo.FeatureTests.csproj --logger "trx;LogFileName=TestResults/hugo-feature-tests.trx"
```

## Best Practices

- **Drive time, don’t wait.** Use `FakeTimeProvider` to advance timers (`Go/Time/TimerTests.cs`) instead of `Task.Delay`.
- **Propagate `TestContext.Current.CancellationToken`.** Every async helper in `Hugo.Go` expects the ambient token; pass it through selects, wait groups, and channel reads.
- **Use xUnit collections for serialization.** Reuse `GoTestsCollection` when tests mutate shared static state or depend on ordering.
- **Keep assertions behavioral.** Check channel contents, ordering, and error codes instead of private fields.
- **Name tests with intent.** Follow the `Scenario_Condition_ShouldOutcome` pattern for quick scanning.
- **Share helpers when orchestration repeats.** Inline functions (`MakeChannel`, `SelectBuilder`) keep specs readable.

## Scenarios & Examples

### 1. Select builder orchestration (`tests/Hugo.FeatureTests/Go/General/GoTests.General.cs`)

```csharp
var channel = MakeChannel<int>();
channel.Writer.TryWrite(9);
channel.Writer.TryComplete();

var result = await Select<int>(cancellationToken: TestContext.Current.CancellationToken)
    .Case(channel.Reader, static value => value)
    .Default(static () => 0)
    .ExecuteAsync();

result.IsSuccess.ShouldBeTrue();
result.Value.ShouldBe(9);
```

This pattern demonstrates how select builders prioritize ready cases over defaults and is ideal for documenting fan-in behavior.

### 2. Channel builders with explicit policies (`tests/Hugo.FeatureTests/Go/Channels/ChannelBuilderTests.cs`)

```csharp
var channel = BoundedChannel<int>(capacity: 1)
    .WithFullMode(BoundedChannelFullMode.DropOldest)
    .Build();

channel.Writer.TryWrite(1).ShouldBeTrue();
channel.Writer.TryWrite(2).ShouldBeTrue();
channel.Reader.TryRead(out var value).ShouldBeTrue();
value.ShouldBe(2);
```

Use this template when validating bounded capacity, drop modes, or prioritized channels without spinning real workers.

### 3. Execution policies with timeouts (`tests/Hugo.FeatureTests/Go/ExecutionPolicies/GoTests.ExecutionPolicies.cs`)

```csharp
var provider = new FakeTimeProvider();

var result = await WithTimeoutAsync(
    static async ct =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Ok(42);
    },
    TimeSpan.FromSeconds(1),
    timeProvider: provider,
    TestContext.Current.CancellationToken);

result.IsFailure.ShouldBeTrue();
result.Error?.Code.ShouldBe(ErrorCodes.Timeout);
```

Tie timeout-oriented assertions to `FakeTimeProvider` to keep policies repeatable.

### 4. Timer pipelines (`tests/Hugo.FeatureTests/Go/Time/TimerTests.cs`)

```csharp
var provider = new FakeTimeProvider();
var reader = After(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

var readTask = reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
readTask.IsCompleted.ShouldBeFalse();

provider.Advance(TimeSpan.FromSeconds(5));
 (await readTask).ShouldBe(42);
```

Feature tests should cover lifecycle events—single-fire timers, cancellable tickers, and backpressure on channel readers.

## Continuous Improvement

- Add new feature tests alongside the features they protect.
- When chasing a bug, reproduce it here with a failing scenario before fixing the implementation.
- Update this README as new orchestration patterns, helpers, or best practices emerge.
