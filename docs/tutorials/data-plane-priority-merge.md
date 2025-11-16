# Priority-Aware Merging of Streaming Channels

When merging multiple streaming sources you may want more control than "read whichever channel has data." This tutorial shows how to build a priority-aware merge strategy on top of `ResultPipelineChannels.Select<TResult>` and `ForEachAsync`.

## Scenario

You have three channel readers:

- `alerts` – must drain immediately.
- `premium` – higher priority than `standard`.
- `standard` – bulk traffic.

You want to merge them into a single stream that always prefers higher priorities while still allowing lower priorities to flow when the higher ones are empty.

## Building a Priority Merge

1. Wrap each reader in a `ChannelCase<Result<Message>>` with a priority value.
2. Use `ResultPipelineChannels.Select<TResult>` inside a loop to pick the next item.
3. Push the selected item into a destination channel.

```csharp
Channel<Message> merged = Channel.CreateUnbounded<Message>();

var builder = ResultPipelineChannels.Select<Result<Message>>(context);
builder.Case(alerts, priority: 0, (message, _) => Result.Ok(Result.Ok(message)));
builder.Case(premium, priority: 1, (message, _) => Result.Ok(Result.Ok(message)));
builder.Case(standard, priority: 2, (message, _) => Result.Ok(Result.Ok(message)));

_ = Task.Run(async () =>
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var result = await builder.ExecuteAsync();
        if (result.IsFailure)
        {
            merged.Writer.TryComplete(result.Error);
            break;
        }

        if (result.Value.IsSuccess)
        {
            await merged.Writer.WriteAsync(result.Value.Value, cancellationToken);
        }
    }
}, cancellationToken);
```

Downstream consumers can now read from `merged.Reader` knowing that alerts/premium traffic will always win ties.

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

- Priority merging is achieved by reusing the select builder with explicit priority values.
- Wrap the loop in a background task to keep producing into a destination channel.
- Downstream stages can stay completely oblivious to the priority logic because they simply consume the merged channel.
