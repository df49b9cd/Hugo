# Hugo.IntegrationTests – Integration Testing Guide

`tests/Hugo.IntegrationTests` validates the runtime contracts that emerge only when multiple Hugo components collaborate (task queues, deterministic gates, workflow execution, and state stores). Use this guide when adding or updating integration specs.

## Scope

- Exercise public APIs that span more than one subsystem: task queue leasing, deterministic gates/effects, version gates, safe task queues, and workflow execution contexts.
- Run everything against in-memory implementations (`InMemoryDeterministicStateStore`, `TaskQueue<T>`, `WorkflowExecutionContext`) while preserving the async/concurrency behavior of production code.
- Exclude single-component contracts (covered by `tests/Hugo.UnitTests`) and full stack scenarios (covered by `tests/Hugo.FeatureTests` or deterministic provider suites).

## Goals

- Detect regressions in cross-component flows—e.g., how a `TaskQueue` interacts with leases, how `DeterministicGate` drives upgrades, or how workflows emit metadata.
- Provide executable examples of recommended usage patterns for queues, gates, and workflow state transitions.
- Keep the suite deterministic so it can run in CI on every target framework (currently `net9.0` and `net10.0`).

## Reasoning

Many bugs appear only when several deterministic primitives cooperate: a gate relies on version snapshots, task queues depend on lease sweepers, workflows coordinate logical clocks. Integration tests assemble those pieces exactly as clients do, but with in-memory stores and fake time so we can iterate quickly without external dependencies.

## Methodology & Approach

### Principles

1. **Mirror production wiring.** Instantiate real `TaskQueue<T>`, `DeterministicGate`, `WorkflowExecutionContext`, etc., with the same options consumers would set.
2. **Favor deterministic dependencies.** Use `FakeTimeProvider`, `InMemoryDeterministicStateStore`, and lightweight dead-letter handlers to avoid flakiness.
3. **Validate observable contracts.** Focus on pending counts, error codes, metadata, upgrade decisions, and logical clock values rather than private details.

### Test Taxonomy

| Area | Files | Notes |
| ---- | ----- | ----- |
| Task queues & safe dispatch | `TaskQueueTests.cs`, `SafeTaskQueueTests.cs`, `TaskQueueChannelAdapterTests.cs`, `TaskQueueTestsCollection.cs` | Lease lifecycle, backpressure, dead-letter handling, channel adapters, and serialized collections for concurrency. |
| Deterministic gates & effects | `DeterministicGateTests.cs`, `DeterministicEffectStoreTests.cs`, `VersionGateTests.cs` | Ensures upgrades, retries, and effect deduplication operate with virtual time/state stores. |
| Workflow execution | `WorkflowExecutionContextTests.cs` | Covers metadata propagation, logical clocks, completion snapshots, and replay counters. |
| Supporting primitives | `InMemoryDeterministicStateStoreTests.cs` (shared), `DeterministicRecord` usages | Validate state persistence expectations when composed with gates and effects. |

### Tooling

- **xUnit.net v3** for the test runner and `[Collection]` attributes (e.g., `TaskQueueConcurrency`) to serialize shared resources.
- **Microsoft.Extensions.TimeProvider.Testing** (`FakeTimeProvider`) for time travel within queues, gates, and workflows.
- **coverlet.collector** to emit coverage artifacts via `dotnet test --collect:"XPlat Code Coverage"`.
- **FsCheck** packages are available if property-based coverage is needed for future scenarios.

## Setup

1. Install the .NET 10 SDK.
2. Restore dependencies: `dotnet restore Hugo.slnx`.
3. Allow preview TFMs if necessary: `export DOTNET_ROLL_FORWARD=Major`.

## Execution

Run the entire integration suite:

```bash
dotnet test tests/Hugo.IntegrationTests/Hugo.IntegrationTests.csproj
```

Target a single framework:

```bash
dotnet test tests/Hugo.IntegrationTests/Hugo.IntegrationTests.csproj -f net9.0
```

Filter by namespace/class:

```bash
dotnet test tests/Hugo.IntegrationTests/Hugo.IntegrationTests.csproj --filter "FullyQualifiedName~TaskQueueTests"
dotnet test tests/Hugo.IntegrationTests/Hugo.IntegrationTests.csproj --filter "WorkflowExecutionContextTests"
```

Collect coverage / diagnostics:

```bash
dotnet test tests/Hugo.IntegrationTests/Hugo.IntegrationTests.csproj \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=lcov \
  /p:CoverletOutput=./TestResults/coverage

dotnet test tests/Hugo.IntegrationTests/Hugo.IntegrationTests.csproj --logger "trx;LogFileName=TestResults/hugo-integration-tests.trx"
```

## Best Practices

- **Use fake time everywhere.** Advance leases, heartbeats, and workflow clocks with `FakeTimeProvider` instead of `Task.Delay`.
- **Share fixtures for concurrent resources.** Apply `[Collection("TaskQueueConcurrency")]` or custom collections when tests mutate shared queues.
- **Propagate `TestContext.Current.CancellationToken`.** Pass it through every async call so cancellation behavior stays covered.
- **Validate both sides of contracts.** After enqueuing and leasing, verify counts (`PendingCount`, `ActiveLeaseCount`) as well as return values.
- **Model realistic metadata.** Workflow tests should populate attributes, schedules, and retries to catch serialization regressions.
- **Prefer helpers over duplication.** Reuse local `CreateContext`, `CreateGate`, or dead-letter helpers when scenarios repeat.

## Scenarios & Examples

### 1. Task queue leasing (`tests/Hugo.IntegrationTests/TaskQueueTests.cs`)

```csharp
var provider = new FakeTimeProvider();
await using var queue = new TaskQueue<string>(timeProvider: provider);

await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

await lease.CompleteAsync(TestContext.Current.CancellationToken);

queue.PendingCount.ShouldBe(0);
queue.ActiveLeaseCount.ShouldBe(0);
```

This pattern validates enqueue/lease/complete flows while observing queue metrics.

### 2. Deterministic gate upgrades (`tests/Hugo.IntegrationTests/DeterministicGateTests.cs`)

```csharp
var store = new InMemoryDeterministicStateStore();
var provider = new FakeTimeProvider();
var gate = new DeterministicGate(
    new VersionGate(store, provider),
    new DeterministicEffectStore(store, provider));

var result = await gate.ExecuteAsync(
    "change.v1",
    currentVersion: 1,
    upgradedVersion: 2,
    upgraded: _ => Task.FromResult(Result.Ok(42)),
    legacy: _ => Task.FromResult(Result.Ok(7)),
    metadata: null,
    TestContext.Current.CancellationToken);

result.IsSuccess.ShouldBeTrue();
result.Value.ShouldBe(42);
```

Use this structure when asserting upgrade/legacy decision logic and effect deduplication.

### 3. Workflow metadata snapshots (`tests/Hugo.IntegrationTests/WorkflowExecutionContextTests.cs`)

```csharp
var metadata = new Dictionary<string, string> { ["region"] = "us-east" };
var context = CreateContext(metadata: metadata);

using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);
var snapshot = context.SnapshotVisibility();

snapshot.Status.ShouldBe(WorkflowStatus.Active);
snapshot.Attributes["region"].ShouldBe("us-east");
```

Great for verifying logical clocks, visibility snapshots, and completion metadata.

### 4. Safe task queue dead-lettering (`tests/Hugo.IntegrationTests/SafeTaskQueueTests.cs`)

```csharp
var provider = new FakeTimeProvider();
var deadLetters = new List<string>();

await using var queue = new SafeTaskQueue<string>(
    TaskQueueOptions.Default with { TimeProvider = provider },
    async (context, ct) =>
    {
        deadLetters.Add(context.Value);
        await ValueTask.CompletedTask;
    });

await queue.EnqueueAsync("gamma", TestContext.Current.CancellationToken);
await queue.AbandonAsync("gamma", Error.From("fatal", ErrorCodes.TaskQueueAbandoned), TestContext.Current.CancellationToken);

deadLetters.ShouldContain("gamma");
```

Demonstrates how integration tests cover failure paths and dead-letter handlers end-to-end.

## Continuous Improvement

- When fixing a bug that spans multiple components, reproduce it here first.
- Keep this README updated whenever we add new orchestrations (e.g., new adapters or workflow features).
- Ensure every new integration test documents the scenario it protects so future contributors can build on it confidently.
