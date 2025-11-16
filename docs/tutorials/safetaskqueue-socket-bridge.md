# SafeTaskQueue → Socket Bridge (with gRPC fallback)

`SafeTaskQueue<T>` provides durable leasing and poison-tracking. Pair it with Hugo’s channel adapters to drive long-lived socket or gRPC connections deterministically. This tutorial covers:

- Draining a `SafeTaskQueue<Command>` with multiple lease pumps.
- Routing commands into either a socket writer or gRPC duplex stream.
- Recording compensations so failed sends requeue gracefully.
- Handling poison messages, retries, and observability.

## Architecture

```
SafeTaskQueue<Command>
    │
    ├─ TaskQueueChannelAdapter.Create(queue, concurrency: N)
    │        (multiple PumpAsync loops)
    │
    └─ Result.MapStreamAsync(lease channel)
             ├─ SafeTaskQueue semantics (Ack/Fail)
             ├─ Result.Pipeline.WithTimeoutAsync + Retry
             ├─ fan-out to socket vs gRPC
             └─ compensations → Requeue or mark poison
```

## Setup

```csharp
var queue = SafeTaskQueue<Command>.Create(
    new SafeTaskQueueOptions
    {
        MaxRetries = 5,
        PoisonQueueName = "socket-poison"
    });

var adapter = TaskQueueChannelAdapter<Command>.Create(
    queue,
    concurrency: 4,
    ownsQueue: false);
```

Each adapter pump leases a `Command`, writes it to a `Channel<TaskQueueLease<Command>>`, and re-queues on failure.

## Processing Pipeline

```csharp
var ctx = _contextFactory.Create("socket-bridge", cancellationToken);

await adapter.Reader
    .ReadAllAsync(cancellationToken)
    .Select(lease => Result.Ok(lease))
    .ForEachAsync(async (leaseResult, token) =>
    {
        if (leaseResult.IsFailure)
        {
            return leaseResult.CastFailure<Unit>();
        }

        var lease = leaseResult.Value;
        ctx.RegisterCompensation(_ => queue.FailAsync(lease, Error.From("rollback"), requeue: true, token));

        var sendResult = await SendWithFallbackAsync(lease.Payload, ctx, token);
        if (sendResult.IsFailure)
        {
            await queue.FailAsync(lease, sendResult.Error!, requeue: !lease.IsPoisoned, token);
            return sendResult.CastFailure<Unit>();
        }

        await queue.AckAsync(lease, token);
        return Result.Ok(Unit.Value);
    }, cancellationToken);
```

### Send with Socket + gRPC fallback

```csharp
async ValueTask<Result<Unit>> SendWithFallbackAsync(
    Command command,
    ResultPipelineStepContext ctx,
    CancellationToken cancellationToken)
{
    var socketResult = await ResultPipeline.WithTimeoutAsync(
        async innerToken => await _socketWriter.SendAsync(command, innerToken),
        timeout: TimeSpan.FromSeconds(3),
        cancellationToken: cancellationToken);

    if (socketResult.IsSuccess)
    {
        return socketResult;
    }

    ctx.RegisterCompensation(_ => _socketWriter.RollbackAsync(command.Id, cancellationToken));

    return await ResultPipeline.RetryAsync(
        (_, token) => SendGrpcAsync(command, token),
        policy: ResultExecutionPolicy.ExponentialRetry(3, TimeSpan.FromMilliseconds(250)),
        cancellationToken: cancellationToken);
}

async ValueTask<Result<Unit>> SendGrpcAsync(Command command, CancellationToken token)
{
    using var call = _grpcClient.ExecuteCommand();

    var writeResult = await Result.TryAsync(async _ =>
    {
        await call.RequestStream.WriteAsync(command, token);
        await call.RequestStream.CompleteAsync();
        return Unit.Value;
    }, token);

    if (writeResult.IsFailure)
    {
        return writeResult;
    }

    return await Result.MapStreamAsync(call.ResponseStream.ReadAllAsync(token), (reply, _) => Result.Ok(reply), token)
        .ForEachAsync(async (replyResult, ct) =>
        {
            if (replyResult.IsFailure)
            {
                return replyResult.CastFailure<Unit>();
            }

            return Result.Ok(Unit.Value);
        }, token);
}
```

## Poison Handling & Metrics

- `SafeTaskQueue` marks items as poison after `MaxRetries`. Use `TapFailureEachAsync` to log them and route to `queue.MoveToPoisonAsync`.
- Track queue depth, active leases, and success/failure counts via `TapSuccessEachAsync` and `TapFailureEachAsync` on the `ForEachAsync` pipeline.

## Heartbeats

Use `ResultPipelineTimers.NewTicker` per connection to send heartbeat frames. If the heartbeat fails, the compensation scope re-queues the active lease before the pump exits.

## Summary

SafeTaskQueue + TaskQueueChannelAdapter gives you durable backpressure and replay semantics. Hugo’s Result pipeline stitches this into sockets or gRPC streams with retries, compensations, and observability, ensuring that even catastrophic disconnects don’t lose in-flight work. 
