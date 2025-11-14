---
title: "Migrate OmniRelay backpressure to Hugo TaskQueueBackpressureMonitor"
description: "Replace bespoke ResourceLease backpressure plumbing with Hugo’s built-in monitor, rate limiter selector, and diagnostics listeners."
---

# Migrate OmniRelay backpressure to Hugo

OmniRelay’s dispatcher currently wires `ResourceLeaseBackpressureSignal` into custom rate limiter and diagnostics components. Hugo’s `TaskQueueBackpressureMonitor<T>` provides those same primitives directly over `TaskQueue<T>` / `SafeTaskQueueWrapper<T>` so hosts can delete bespoke plumbing.

## 1. Attach the monitor

```csharp
await using var queue = new TaskQueue<OmniRelayDispatch>(new TaskQueueOptions
{
    Name = "dispatcher",
    Capacity = 4096,
    LeaseDuration = TimeSpan.FromSeconds(15)
});

await using var monitor = new TaskQueueBackpressureMonitor<OmniRelayDispatch>(queue, new TaskQueueBackpressureMonitorOptions
{
    HighWatermark = 512,
    LowWatermark = 192,
    Cooldown = TimeSpan.FromSeconds(10)
});
```

The monitor invokes `TaskQueue.ConfigureBackpressure` internally so you don’t have to pass `TaskQueueBackpressureOptions` manually.

## 2. Mirror the legacy rate limiter selector

```csharp
var limiter = new BackpressureAwareRateLimiter(
    unthrottledLimiter: new ConcurrencyLimiter(new ConcurrencyLimiterOptions(
        permitLimit: 256,
        queueLimit: 0,
        queueProcessingOrder: QueueProcessingOrder.OldestFirst)),
    backpressureLimiter: new ConcurrencyLimiter(new ConcurrencyLimiterOptions(
        permitLimit: 16,
        queueLimit: 512,
        queueProcessingOrder: QueueProcessingOrder.OldestFirst)),
    disposeUnthrottledLimiter: true,
    disposeBackpressureLimiter: true);

using var rateLimiterSubscription = monitor.RegisterListener(limiter);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.Get("dispatcher", _ => limiter.LimiterSelector()));
});
```

`LimiterSelector` returns whichever `RateLimiter` matches the current signal, giving you the same middleware hook OmniRelay uses today without custom structs.

## 3. Stream diagnostics to the control plane

```csharp
await using var diagnostics = new TaskQueueBackpressureDiagnosticsListener(capacity: 256);
using var diagnosticsSubscription = monitor.RegisterListener(diagnostics);

app.MapGet("/omnirelay/backpressure", async context =>
{
    await foreach (var signal in diagnostics.Reader.ReadAllAsync(context.RequestAborted))
    {
        await context.Response.WriteAsJsonAsync(signal, context.RequestAborted);
    }
});
```

The listener’s `Latest` property exposes the current snapshot for quick status checks, while the channel gives you an ordered stream suitable for SSE/gRPC.

## 4. Await draining before rebalancing

When the monitor flips into backpressure, call `WaitForDrainingAsync` from your enqueue paths or rolling-upgrade hooks instead of replicating the loop from `ResourceLeaseDispatcher`:

```csharp
if (monitor.IsActive)
{
    var cleared = await monitor.WaitForDrainingAsync(context.RequestAborted);
    logger.LogInformation("Backpressure cleared at {Depth}", cleared.PendingCount);
}
```

## 5. Observe the new metrics

`GoDiagnostics` now emits the following instruments for dashboards and alerts:

- `hugo.taskqueue.backpressure.active` – up/down count of queues currently throttled (tagged by `taskqueue.name`).
- `hugo.taskqueue.backpressure.pending` – histogram of pending depth when transitions fire.
- `hugo.taskqueue.backpressure.transitions` – counter per transition event.
- `hugo.taskqueue.backpressure.duration` – histogram of durations spent in the previous state.

Reuse your existing `ResourceLeaseMetrics` dashboards by swapping the metric name prefix and verifying the new metadata (`taskqueue.name` vs. the old tenant tags).

After these changes you can delete OmniRelay’s `ResourceLeaseBackpressure*` files—the monitor, diagnostics listener, limiter selector, and metrics provide the same behavior natively inside Hugo.
