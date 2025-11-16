# Batching and Windowing Streaming Results

Many streaming workloads need to batch records (e.g., 500 items or every 2 seconds). This tutorial demonstrates how to build a deterministic batching stage using `ResultPipelineChannels.WindowAsync` and Result combinators.

## Scenario

You receive `Result<Event>` instances. You must:

- Buffer up to 500 events or flush every 2 seconds, whichever happens first.
- Persist the batch atomically.
- Roll back partial writes if persistence fails.

## Building Blocks

- `ResultPipelineChannels.WindowAsync` to slice a source channel into deterministic batches (size threshold, flush interval, or both).
- `Result.MapStreamAsync` + `ForEachAsync` to consume windowed results and persist them.
- `ResultPipelineStepContext` to capture compensations per flushed batch.

## Step 1 – Convert the Stream Into Batches

```csharp
var batchingContext = new ResultPipelineStepContext("event-batcher", scope, context.TimeProvider, ct);
var windows = ResultPipelineChannels.WindowAsync(
    batchingContext,
    _eventChannel.Reader,
    batchSize: 500,
    flushInterval: TimeSpan.FromSeconds(2),
    cancellationToken: ct);
```

- The adapter links to `ctx.TimeProvider`, so deterministic tests can advance virtual time.
- When either condition is met (size or interval), the adapter writes `IReadOnlyList<Event>` into the returned `ChannelReader`.

## Step 2 – Process Each Window With Compensations

```csharp
await Result.MapStreamAsync(
        windows.ReadAllAsync(ct),
        (batch, token) => Result.Ok(batch),
        ct)
    .ForEachAsync(async (result, token) =>
    {
        if (result.IsFailure)
        {
            return result.CastFailure<Unit>();
        }

        var batch = result.Value;
        batchingContext.RegisterCompensation(async cancel =>
        {
            await _repository.DeleteBatchAsync(batch, cancel);
        });

        var persisted = await _repository.WriteBatchAsync(batch, token);
        if (persisted.IsFailure)
        {
            return persisted.CastFailure<Unit>();
        }

        return Result.Ok(Unit.Value);
    },
    ct);
```

The compensation scope now contains per-batch rollback actions that replay automatically if downstream stages fail.

## Step 3 – Implement Flush With Compensations

```csharp
private async ValueTask<Result<Unit>> FlushBatchAsync(ResultPipelineStepContext context, IReadOnlyList<Event> batch, CancellationToken cancellationToken)
{
    return await Result.TryAsync(async ct =>
    {
        context.RegisterCompensation(async token =>
        {
            await _repository.DeleteBatchAsync(batch, token);
        });

        await _repository.WriteBatchAsync(batch, ct);
        return Unit.Value;
    }, cancellationToken);
}
```

## Summary

- `ResultPipelineChannels.WindowAsync` handles buffering, size thresholds, and timeouts using the pipeline’s `TimeProvider`.
- `ResultPipelineStepContext` scoped to the batch ensures compensations (e.g., deleting batches) replay if persistence fails.
- After batching, continue chaining Result combinators (`TapAsync`, `RetryAsync`, etc.) exactly as you would with any other pipeline stage.
