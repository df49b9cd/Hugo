# Priority-Aware Merging of Streaming Channels

When merging multiple streaming sources you may want more control than "read whichever channel has data." This tutorial shows how to build a priority-aware merge strategy using `ResultPipelineChannels.MergeWithStrategyAsync` and `ForEachAsync`.

## Scenario

You have three channel readers:

- `alerts` – must drain immediately.
- `premium` – higher priority than `standard`.
- `standard` – bulk traffic.

You want to merge them into a single stream that always prefers higher priorities while still allowing lower priorities to flow when the higher ones are empty.

## Building a Priority Merge

1. Create a delegate that returns the next reader index based on your priority scheme.
2. Pass the delegate to `ResultPipelineChannels.MergeWithStrategyAsync` along with the destination writer.

```csharp
Channel<Message> merged = Channel.CreateUnbounded<Message>();

Func<IReadOnlyList<ChannelReader<Message>>, CancellationToken, ValueTask<int>> strategy = (readers, _) =>
{
    if (!readers[0].Completion.IsCompleted)
    {
        return new ValueTask<int>(0); // alerts
    }

    if (!readers[1].Completion.IsCompleted)
    {
        return new ValueTask<int>(1); // premium
    }

    return new ValueTask<int>(2); // standard
};

var mergeResult = await ResultPipelineChannels.MergeWithStrategyAsync(
    context,
    new[] { alerts, premium, standard },
    merged.Writer,
    strategy,
    cancellationToken: cancellationToken);

if (mergeResult.IsFailure)
{
    await Result.RunCompensationAsync(policy, scope, cancellationToken);
    return mergeResult.CastFailure<Unit>();
}
```

- The strategy is called repeatedly; for each invocation you decide which reader should be polled next.
- When every reader completes, the adapter automatically completes the destination writer (unless `completeDestination = false`).

## Streaming Consumption

Once merged, continue with `Result.MapStreamAsync` + `ForEachAsync` as usual:

```csharp
await merged.Reader
    .ReadAllAsync(cancellationToken)
    .Select(message => Result.Ok(message))
    .ForEachAsync(async (result, token) =>
    {
        if (result.IsFailure)
        {
            return result.CastFailure<Unit>();
        }

        await _processor.HandleAsync(result.Value, token);
        return Result.Ok(Go.Unit.Value);
    }, cancellationToken);
```

## Summary

- Priority merging is achieved by supplying a delegate to `ResultPipelineChannels.MergeWithStrategyAsync` that chooses the next reader.
- The adapter handles cancellation, compensations, and destination completion so downstream stages simply consume the merged channel.
- Continue combining the merged stream with functional combinators (`FlatMapStreamAsync`, `TapSuccessEachAsync`, `FilterStreamAsync`, etc.) to build richer pipelines.
