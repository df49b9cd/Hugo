---
title: "Expose TaskQueue diagnostics endpoints"
description: "Replace OmniRelay’s bespoke ResourceLease diagnostics with Hugo.TaskQueues.Diagnostics meters, activity sources, and SSE endpoints."
---

# TaskQueue diagnostics

`Hugo.TaskQueues.Diagnostics` packages the queues-only instrumentation OmniRelay depended on (meter registration, rate-limited tracing, replication/backpressure listeners) so you can expose control-plane diagnostics without maintaining forks of `ResourceLease*` utilities.

## 1. Install the package

Add a project reference (or NuGet dependency) to `Hugo.TaskQueues.Diagnostics` alongside `Hugo` and `Hugo.TaskQueues.Replication`.

```xml
<ProjectReference Include="..\..\src\Hugo.TaskQueues.Diagnostics\Hugo.TaskQueues.Diagnostics.csproj" />
```

## 2. Register meters, activity sources, and tag enrichers

Use the `IMeterFactory.AddTaskQueueDiagnostics(...)` extension to configure `GoDiagnostics` once. `TaskQueueMetricsOptions` toggles the metric families and injects consistent tags that existing dashboards can pivot on.

```csharp
builder.Services.AddSingleton<IMeterFactory>(_ => new MeterFactory());

builder.Services.AddSingleton(sp =>
{
    var meterFactory = sp.GetRequiredService<IMeterFactory>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    return new TaskQueueDiagnosticsHost(meterFactory, options =>
    {
        options.Metrics.ServiceName = env.ApplicationName;
        options.Metrics.DefaultShard = Environment.GetEnvironmentVariable("REGION") ?? "dispatch-a";
        options.Metrics.EnabledGroups = TaskQueueMetricGroups.All;
        options.Metrics.TagEnrichers.Add((in TaskQueueTagContext ctx, ref TagList tags) =>
        {
            tags.Add("omnirelay.cluster", Environment.GetEnvironmentVariable("CLUSTER"));
        });
    });
});
```

`TaskQueueDiagnosticRegistration` (returned by the extension) implements `IAsyncDisposable`, so hosting frameworks dispose the meter, activity source, and sampling listener automatically.

## 3. Attach monitors and replication sources with zero boilerplate

`TaskQueueDiagnosticsHost` exposes `Attach` overloads that subscribe to `TaskQueueBackpressureMonitor<T>` and `TaskQueueReplicationSource<T>` instances and mux their events into a single channel, perfect for SSE/gRPC endpoints. The ASP.NET Core sample at `samples/Hugo.TaskQueueDiagnosticsHostSample/Program.cs` wires the host up and exposes `/diagnostics/taskqueue` as a streaming endpoint:

```csharp
app.MapGet("/diagnostics/taskqueue", async (TaskQueueDiagnosticsHost diagnostics, HttpResponse response, CancellationToken token) =>
{
    response.Headers.CacheControl = "no-store";
    response.Headers["Content-Type"] = "text/event-stream";

    await foreach (TaskQueueDiagnosticsEvent evt in diagnostics.Events.ReadAllAsync(token))
    {
        await response.WriteAsync($"data: {JsonSerializer.Serialize(evt)}\n\n", token);
        await response.Body.FlushAsync(token);
    }
});
```

OmniRelay’s `control watch backpressure` command can now point at `/diagnostics/taskqueue` without translating the payloadâ€”`TaskQueueBackpressureDiagnosticsEvent` includes the same fields (`IsActive`, `PendingCount`, watermarks) emitted by the previous `ResourceLeaseBackpressureDiagnosticsListener`.

## 4. Dashboards and alerts

The new tag enrichers keep existing dashboards intact:

- **Backpressure transitions:** `sum(increase(hugo_taskqueue_backpressure_transitions_total{service_name="omnirelay-control", taskqueue_name="dispatch"}[5m])) by (taskqueue_shard)` highlights shards that flapped in the last five minutes.
- **Pending depth:** `histogram_quantile(0.95, sum(rate(taskqueue_pending_depth_bucket{taskqueue_name="dispatch"}[5m])) by (le, taskqueue_shard))` mirrors the depth percentiles plotted by the legacy ResourceLease dashboards.
- **Replication lag:** `max(taskqueue_replication_wall_clock_lag_ms{taskqueue_shard="dispatch-a"})` fuels latency burn-down charts and alert rules (`> 5_000 ms` for five consecutive minutes).
- **Throughput / failures:** pivot `taskqueue.failed`, `taskqueue.completed`, and `taskqueue.deadlettered` by `service.name` and `taskqueue.shard` to keep SLA widgets unchanged.

Because `TaskQueueReplicationSource<T>` now supports `RegisterObserver`, instrumenting replication lag no longer requires a secondary queue listenerâ€”attach the diagnostics host and the metrics emit automatically.

## 5. Migration notes

- Delete bespoke `ResourceLeaseBackpressureDiagnosticsListener` and `ResourceLeaseReplicationMetrics` once the new SSE endpoint is live; the sample host exercises all required endpoints in `samples/Hugo.TaskQueueDiagnosticsHostSample`.
- OmniRelay dashboards donâ€™t need schema changes; swap the old metric prefix for the `taskqueue.*`/`hugo.taskqueue.backpressure.*` instruments documented in [Diagnostics Reference](../reference/diagnostics.md).
- For unit/integration tests, dispose the `TaskQueueDiagnosticsRegistration` returned by `AddTaskQueueDiagnostics` or call `GoDiagnostics.Reset()` to ensure per-test isolation.
