# One-Way Socket Pipelines

One-way (fire-and-forget) sockets push messages without waiting for replies. Hugo lets you batch, rate-limit, and compensate queued packets while keeping the write path fluent.

## Scenario

- Each sensor upload is queued locally.
- A pump drains the queue and writes to an upstream collector socket.
- Messages should be retried a few times; if they still fail, the work item is re-queued with a delay.
- Heartbeats must go out every 5s so the collector keeps the connection open.

## Architecture

```
TaskQueueChannelAdapter (pump)
     │
     ▼
Result.MapStreamAsync(queue leases)
     │
     ├─ ResultPipelineChannels.WindowAsync (batch 100 / 1s)
     ├─ ResultPipeline.RetryAsync (per batch)
     └─ Heartbeat ticker (ResultPipelineTimers.NewTicker)
```

## Implementation Highlights

```csharp
var adapter = TaskQueueChannelAdapter<Upload>.Create(_queue, concurrency: 4);

var writer = adapter.Reader
    .ReadAllAsync(ct)
    .Select(Result.Ok)
    .WindowAsync(size: 100, flushInterval: TimeSpan.FromSeconds(1))
    .ForEachAsync(async (batchResult, token) =>
    {
        if (batchResult.IsFailure)
        {
            await adapter.Queue.NackAsync(batchResult.Error!);
            return batchResult.CastFailure<Unit>();
        }

        var retryResult = await ResultPipeline.RetryAsync(
            (ctx, innerToken) => SendBatchAsync(batchResult.Value, ctx, innerToken),
            policy: ResultExecutionPolicy.ExponentialRetry(3, TimeSpan.FromMilliseconds(500)),
            cancellationToken: token);

        return retryResult;
    }, ct);
```

### Heartbeats

```csharp
var ticker = ResultPipelineTimers.NewTicker(ctx, TimeSpan.FromSeconds(5), ct);
_ = ticker.Reader.ForEachAsync(async (_, token) =>
{
    await _socket.WriteAsync(HeartbeatFrame, token);
    return Result.Ok(Unit.Value);
});
```

If the heartbeat fails, the pump’s compensation scope closes the socket and enqueues pending uploads for later.

### Error Handling

- Use `Result.CollectErrorsAsync` on the upload stream to aggregate failures for observability.
- Register compensations to release queue leases or mark items for dead-lettering.

## When to Use

One-way pipelines shine for telemetry, logging, or notifications where throughput matters more than synchronous replies. Hugo’s streaming combinators give you batching, retries, throttling, and heartbeats without leaving the Result discipline. 
