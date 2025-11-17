# Coordinate Fan-In Workflows

Use this guide when you need to merge multiple channel readers into a single pipeline, preserve ordering guarantees, and stop cleanly on cancellation or faults.

## Goal

Create a relay that:

- Accepts messages from multiple bounded channels.
- Forwards payloads to a destination channel with deterministic completion semantics.
- Reports cancellation or faults through a `Result<Unit>` so callers can react.

Result: A consolidated reader that downstream consumers can process sequentially without juggling multiple tokens or completion signals.

## Prerequisites

- .NET 10 SDK.
- A reference to the `Hugo` package.
- Familiarity with [`WaitGroup`](../reference/concurrency-primitives.md#waitgroup) and the tutorial [Getting started with channels and results](../tutorials/getting-started.md).

## Architecture

```
metrics.Reader  ┐
jobs.Reader     ├─> FanInAsync ──► merged.Writer ──► consumer
alerts.Reader   ┘
```

`FanInAsync` coordinates the producers, handling completion semantics and fault propagation so consumers only need to monitor a single reader.

## Step-by-step

### 1. Set up cancellation and channels

```csharp
using Hugo;
using static Hugo.Go;

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

var metrics = MakeChannel<int>(capacity: 16);
var jobs = MakeChannel<string>(capacity: 16);
var alerts = MakeChannel<string>(capacity: 8);
var merged = MakeChannel<object>(capacity: 32);
```

- Use bounded capacities to apply backpressure before the accumulation grows unbounded.
- Keep the destination capacity higher than individual sources when you expect bursts.

### 2. Schedule producers

```csharp
var workers = new WaitGroup();

workers.Go(async () =>
{
    using var complete = Defer(() => metrics.Writer.TryComplete());
    for (var i = 0; i < 10; i++)
    {
        await metrics.Writer.WriteAsync(i, cts.Token).ConfigureAwait(false);
    }
});

workers.Go(async () =>
{
    using var complete = Defer(() => jobs.Writer.TryComplete());
    foreach (var job in new[] { "build", "deploy", "notify" })
    {
        await jobs.Writer.WriteAsync($"job={job}", cts.Token).ConfigureAwait(false);
    }
});

workers.Go(async () =>
{
    using var complete = Defer(() => alerts.Writer.TryComplete());
    await alerts.Writer.WriteAsync("latency-sla", cts.Token).ConfigureAwait(false);
});
```

Each producer wraps `TryComplete` with `Defer` so the reader always sees a completion signal, even if cancellation fires mid-loop.

### 3. Fan-in with `Go.FanInAsync`

```csharp
var relay = FanInAsync(
    sources: new[] { metrics.Reader, jobs.Reader, alerts.Reader },
    destination: merged.Writer,
    completeDestination: true,
    cancellationToken: cts.Token);
```

- `completeDestination: true` completes the destination when all sources finish. Set it to `false` if another component owns the destination lifecycle.
- Exceptions or `TryComplete(error)` calls on a source propagate to the destination writer so downstream consumers observe the failure.

### 4. Consume merged values

```csharp
await foreach (var payload in merged.Reader.ReadAllAsync(cts.Token))
{
    switch (payload)
    {
        case int sample:
            Console.WriteLine($"metric {sample}");
            break;
        case string job when job.StartsWith("job=", StringComparison.Ordinal):
            Console.WriteLine($"workflow {job}");
            break;
        case string alert:
            Console.WriteLine($"alert {alert}");
            break;
    }
}
```

- If you need to prioritise alerts, consider sending them through a dedicated channel and processing them first, or switch to [`MakePrioritizedChannel`](../reference/concurrency-primitives.md#channels).
- Attach result pipelines around each branch when you need validation or transformation.

### 5. Join background work

```csharp
var relayResult = await relay.ConfigureAwait(false);
if (relayResult.IsFailure)
{
    Console.WriteLine($"fan-in aborted: {relayResult.Error}");
}

await workers.WaitAsync(cts.Token).ConfigureAwait(false);
```

`FanInAsync` returning a `Result<Unit>` makes it easy to surface cancellation (`Error.Canceled`), deadlines (`Error.Timeout`), or upstream faults in logs and diagnostics.

## Validate success

- Producers complete their writers even when cancellation is requested.
- The merged reader closes after the last source completes (no hang on `ReadAllAsync`).
- Cancelling `cts` propagates through `FanInAsync`, the consumer loop, and `WaitAsync`.
- Faulting a producer (`TryComplete(new InvalidOperationException(...))`) surfaces in `relayResult.Error`.

## Variations

- **Loop without a destination channel:** Use [`Go.SelectFanInAsync`](../reference/concurrency-primitives.md#selectfaninasync) to run an inline loop when you only need to process messages (no shared destination).
- **Apply deadlines:** Pass the `timeout` and `TimeProvider` overloads on `FanInAsync` to apply a single absolute deadline across the fan-in session.
- **Throttle producers:** Wrap each producer with `Result.RetryWithPolicyAsync` to back off when downstream systems slow down. Observability metadata on `GoDiagnostics` exposes backpressure.
- **Backpressure monitoring:** Call `GoDiagnostics.Configure` and export the `channel.depth` histogram to visualise queue pressure in OpenTelemetry dashboards.

## Troubleshooting

- **Early cancellation:** Link source-specific tokens to the relay token. When the linked token fires, `FanInAsync` completes with `Error.Canceled` and the destination writer completes with the same error.
- **Unbounded growth:** Always pass `BoundedChannelOptions` when calling `MakeChannel`. Select a `FullMode` (e.g. `DropOldest`, `Wait`) that matches your workload.
- **Shared destination completion:** Set `completeDestination` to `false` if another component owns the destination lifecycle; otherwise you'll complete the writer twice.
- **Stalled consumers:** If the consumer cannot keep up, consider increasing the destination `capacity` or adding a dedicated consumer per payload type with [`Result.WhenAll`](../reference/result-pipelines.md#parallel-orchestration-and-retries).

## Related topics

- [Publish metrics and traces to OpenTelemetry](observe-with-opentelemetry.md) to monitor channel depth and fan-in latency.
- [Apply timeout, retry, and cancellation playbooks](playbook-templates.md) for producers that make outbound calls.
- [`Go.FanIn`](../reference/concurrency-primitives.md#fan-in-utilities) when you need Hugo to manage the destination channel lifecycle automatically.
