# Batching and Windowing Streaming Results

Many streaming workloads need to batch records (e.g., 500 items or every 2 seconds). This tutorial demonstrates how to build a deterministic batching stage using `ResultPipelineTimers`, channels, and Result combinators.

## Scenario

You receive `Result<Event>` instances. You must:

- Buffer up to 500 events or flush every 2 seconds, whichever happens first.
- Persist the batch atomically.
- Roll back partial writes if persistence fails.

## Building Blocks

- `Channel<Event>` to hold buffered events.
- `ResultPipelineTimers.NewTicker` for the flush timer.
- `Result.MapStreamAsync` + `ForEachAsync` to consume events and manage batches.

## Step 1 – Initialize Batch State

```csharp
var batch = new List<Event>(500);
var batchScope = new CompensationScope();
var batchContext = new ResultPipelineStepContext("event-batcher", batchScope, TimeProvider.System, ct);

var flushTicker = ResultPipelineTimers.NewTicker(batchContext, TimeSpan.FromSeconds(2), ct);
```

## Step 2 – Consume Events and Trigger Flushes

```csharp
await Result.MapStreamAsync(_eventStream, (result, token) => result, ct)
    .ForEachAsync(async (result, token) =>
    {
        if (result.IsFailure)
        {
            return result.CastFailure<Go.Unit>();
        }

        batch.Add(result.Value);
        if (batch.Count >= 500)
        {
            var flushResult = await FlushBatchAsync(batchContext, batch, token);
            if (flushResult.IsFailure)
            {
                return flushResult;
            }

            batch.Clear();
            batchScope.Clear();
        }

        return Result.Ok(Go.Unit.Value);
    },
    ct);
```

## Step 3 – Handle Timer Flushes

Use a background task to watch the ticker and flush if time expires:

```csharp
_ = Task.Run(async () =>
{
    await foreach (var _ in flushTicker.Reader.ReadAllAsync(ct))
    {
        if (batch.Count == 0)
        {
            continue;
        }

        var flushResult = await FlushBatchAsync(batchContext, batch, ct);
        if (flushResult.IsFailure)
        {
            await Result.RunCompensationAsync(_policy, batchScope, ct);
            break;
        }

        batch.Clear();
        batchScope.Clear();
    }
}, ct);
```

## Step 4 – Implement Flush With Compensations

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

- Use a combination of size thresholds and `ResultPipelineTimers.NewTicker` to deterministically flush batches.
- `ResultPipelineStepContext` scoped to the batch ensures compensations (e.g., deleting batches) replay if persistence fails.
- You can extend this approach with multiple tickers (e.g., per tenant) or additional policies (`ResultExecutionPolicy`) as needed.
