# Per-Item Cancellation in Streaming Pipelines

Some services need individual records to honor their own cancellation scopes (e.g., tenant-specific deadlines). This tutorial shows how to create per-item cancellation tokens while still absorbing compensations into the parent `ResultPipelineStepContext`.

## Scenario

- Each `WorkItem` carries `WorkItem.Timeout`.
- If processing exceeds the timeout, mark the item as failed but continue processing other items.
- Ensure compensations (partial writes) rollback when per-item cancellation fires.

## Technique

1. For each item, create a `CancellationTokenSource` linked to both the pipeline token and the item timeout.
2. Process the item with the linked token.
3. Register compensations that observe the same linked token (for deterministic cancellation).

```csharp
await Result.MapStreamAsync(workStream, (item, token) => Result.Ok(item), ct)
    .ForEachAsync(async (result, token) =>
    {
        if (result.IsFailure)
        {
            return result.CastFailure<Unit>();
        }

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        itemCts.CancelAfter(result.Value.Timeout);

        var childContext = new ResultPipelineStepContext(
            $"worker[{result.Value.Id}]",
            new CompensationScope(),
            context.TimeProvider,
            itemCts.Token);

        var processed = await ProcessAsync(childContext, result.Value, itemCts.Token);
        if (processed.IsFailure)
        {
            await Result.RunCompensationAsync(_policy, childContext.CompensationScope, itemCts.Token);
            return processed.CastFailure<Unit>();
        }

        return Result.Ok(Unit.Value);
    },
    ct);
```

- Each item gets its own linked token, so a timeout only cancels that item.
- `Result.RunCompensationAsync` replays the per-item compensations when needed.

## Summary

- Use `CancellationTokenSource.CreateLinkedTokenSource` per item to scope cancellations precisely.
- Still register compensations on the per-item context so that retries or parent cancellation can replay them.
- `ForEachAsync` provides the hook to implement this pattern without verbose `await foreach` loops.
