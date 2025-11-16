# Per-Item Cancellation in Streaming Pipelines

Some services need individual records to honor their own cancellation scopes (e.g., tenant-specific deadlines). This tutorial shows how to create per-item cancellation tokens while still absorbing compensations into the parent `ResultPipelineStepContext`.

## Scenario

- Each `WorkItem` carries `WorkItem.Timeout`.
- If processing exceeds the timeout, mark the item as failed but continue processing other items.
- Ensure compensations (partial writes) rollback when per-item cancellation fires.

## Technique

1. For each item, create a linked `CancellationToken` scoped to that record.
2. Process the item with the linked token.
3. Register compensations that observe the same token (for deterministic cancellation).

```csharp
await Result.MapStreamAsync(workStream, (item, token) => Result.Ok(item), ct)
    .ForEachLinkedCancellationAsync(async (result, linkedToken) =>
    {
        if (result.IsFailure)
        {
            return result.CastFailure<Unit>();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        timeoutCts.CancelAfter(result.Value.Timeout);

        var childContext = new ResultPipelineStepContext(
            $"worker[{result.Value.Id}]",
            new CompensationScope(),
            context.TimeProvider,
            timeoutCts.Token);

        var processed = await ProcessAsync(childContext, result.Value, timeoutCts.Token);
        if (processed.IsFailure)
        {
            await Result.RunCompensationAsync(_policy, childContext.CompensationScope, timeoutCts.Token);
            return processed.CastFailure<Unit>();
        }

        return Result.Ok(Unit.Value);
    },
    ct);
```

- `ForEachLinkedCancellationAsync` hands you a per-item token already linked to the pipeline token; you can add more scopes (timeouts, tenant tokens) without managing the parent token manually.
- `Result.RunCompensationAsync` replays the per-item compensations when needed.

## Summary

- Use `ForEachLinkedCancellationAsync` to get per-item tokens automatically linked to the pipeline token.
- Optionally wrap each linked token in another `CancellationTokenSource` to add per-record timeouts.
- Still register compensations on the per-item context so that retries or parent cancellation can replay them.
- The helper keeps the fluent style while avoiding repetitive `await foreach` boilerplate.
