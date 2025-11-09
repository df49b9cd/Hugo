# Hugo Worker Sample

This sample demonstrates a resilient telemetry processing pipeline that uses `TaskQueue<T>` and the `SafeTaskQueueWrapper<T>` wrapper to lease work cooperatively, retry failures, and surface dead-lettered payloads. It runs on the .NET generic host with deterministic coordination primitives (`VersionGate`, `DeterministicEffectStore`) and prioritised alert channels.

## Configuration

Queue behaviour is configured via `appsettings.json`. The `TelemetryQueue` section controls the bounded channel backing the queue and lease semantics:

```json
{
  "TelemetryQueue": {
    "Capacity": 512,
    "LeaseDurationMs": 8000,
    "HeartbeatIntervalMs": 1000,
    "LeaseSweepIntervalMs": 500,
    "RequeueDelayMs": 750,
    "MaxDeliveryAttempts": 3
  }
}
```

Key settings:

| Setting                | Description                                                                                           |
|------------------------|-------------------------------------------------------------------------------------------------------|
| `Capacity`             | Maximum buffered items waiting to be leased. Producers will backpressure once the queue is full.      |
| `LeaseDurationMs`      | Initial lease length (milliseconds). Items must be completed or heartbeated before this expires.     |
| `HeartbeatIntervalMs`  | Minimum interval between heartbeats. Set to `0` to allow immediate renewals.                         |
| `LeaseSweepIntervalMs` | How often the queue scans for expired leases.                                                         |
| `RequeueDelayMs`       | Optional delay before requeuing failed/expired items.                                                 |
| `MaxDeliveryAttempts`  | Items exceeding this retry count are dead-lettered via the configured callback.                       |

Update `samples/Hugo.WorkerSample/appsettings.json` or supply overrides through environment variables (e.g., `TelemetryQueue__Capacity=1024`) when deploying.

## Safe Task Queue Usage

`SafeTaskQueueWrapper<T>` wraps `TaskQueue<T>` and returns `Result<Unit>` values instead of throwing exceptions on common failure paths. It is registered in `Program.cs`:

```csharp
builder.Services.Configure<TelemetryQueueOptions>(builder.Configuration.GetSection("TelemetryQueue"));
builder.Services.AddSingleton(sp => new TaskQueue<TelemetryWorkItem>(/* … */));
builder.Services.AddSingleton(sp => new SafeTaskQueueWrapper<TelemetryWorkItem>(
    sp.GetRequiredService<TaskQueue<TelemetryWorkItem>>()));
builder.Services.AddSingleton(sp =>
{
    SafeTaskQueueWrapper<TelemetryWorkItem> safeQueue = sp.GetRequiredService<SafeTaskQueueWrapper<TelemetryWorkItem>>();
    return TaskQueueChannelAdapter<TelemetryWorkItem>.Create(safeQueue.UnsafeQueue, concurrency: 1);
});
```

Producer loops enqueue work and inspect the result:

```csharp
var enqueueResult = await safeQueue.EnqueueAsync(workItem, stoppingToken);
if (enqueueResult.IsFailure)
{
    // Log `enqueueResult.Error` and decide whether to stop or retry.
}
```

The worker processes leases through the channel adapter and converts them to `SafeTaskQueueLease<T>`:

```csharp
await foreach (TaskQueueLease<TelemetryWorkItem> lease in adapter.Reader.ReadAllAsync(stoppingToken))
{
    var safeLease = new SafeTaskQueueLease<TelemetryWorkItem>(lease);
    var complete = await safeLease.CompleteAsync(stoppingToken);
    if (complete.IsFailure)
    {
        // Inspect `complete.Error.Code` (e.g., `error.taskqueue.lease_inactive`) and react accordingly.
    }
}
```

`SafeTaskQueueLease` exposes `CompleteAsync`, `HeartbeatAsync`, and `FailAsync` helpers that convert `ObjectDisposedException` and `InvalidOperationException` into structured errors (`error.taskqueue.disposed`, `error.taskqueue.lease_inactive`). Unhandled exceptions are wrapped with `Error.FromException`.

## Health and readiness

`Program.cs` registers `AddTaskQueueHealthCheck` for the telemetry queue and exposes `/health/ready` plus `/health/live` endpoints via `MapHealthChecks`. Kubernetes-style orchestrators can now block rollouts until `PendingCount` drops below the configured degraded/unhealthy thresholds, preventing data loss during upgrades. The same backpressure callbacks (configured through `TaskQueueOptions.Backpressure`) log warnings whenever depth crosses the high watermark so producers can throttle before the health probe flips to `Degraded`.

## Running the Sample

```bash
dotnet run --project samples/Hugo.WorkerSample/Hugo.WorkerSample.csproj
```

Optional: enable the OpenTelemetry pipeline by uncommenting the block in `Program.cs` and supplying `OTEL_EXPORTER_OTLP_ENDPOINT` / `HUGO_PROMETHEUS_ENABLED` variables.

### Expected Logs

- `TelemetryWorker` info logs for heartbeats, CPU readings, and retries.
- Warnings when the queue dead-letters items or when lease operations fail (`LogQueueOperationFailed`, `LogLeaseOperationFailed`).
- `TelemetryAlertService` emits critical/warning alerts via a prioritised channel.

### Tests

`SafeTaskQueueWrapper<T>` is covered by unit tests in `tests/Hugo.Tests/SafeTaskQueueTests.cs`. Run the full suite:

```bash
dotnet test
```
