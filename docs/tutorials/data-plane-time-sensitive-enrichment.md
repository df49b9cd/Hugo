# Time-Sensitive Enrichment With Pipeline Timers

In latency-critical data planes you often need to enrich events (taxonomy lookups, policy evaluation) within strict deadlines. This tutorial demonstrates how to use `ResultPipelineTimers` and `ResultPipelineChannels.SelectAsync` to enforce per-message deadlines, emit timeout errors, and roll back partially applied enrichment work.

## Scenario

You receive telemetry events that must be enriched by:

1. Fetching tenant configuration (fast cache lookup).
2. Calling a third-party taxonomy API (potentially slow).
3. Writing the enriched event to storage.

You have 150 ms to finish steps (1)-(3). If the enrichment takes longer, the event should be marked as timed out and a compensation should revert any partial writes.

## Tools Used

- `ResultPipelineTimers.DelayAsync` and `ResultPipelineTimers.AfterAsync` for deadline enforcement.
- `ResultPipelineChannels.SelectAsync` to race between real work and a timeout channel.
- `Result<T>.WithCompensation(...)` to capture clean-up when the timeout branch fires.

## Step 1 – Set Up the Pipeline Context

```csharp
var scope = new CompensationScope();
var context = new ResultPipelineStepContext(
    "enrichment",
    scope,
    TimeProvider.System,
    cancellationToken);
```

## Step 2 – Build the Timeout Channel

Use `ResultPipelineTimers.AfterAsync` to get a channel reader that produces a timestamp once the deadline elapses. Wrap it in a `ChannelCase<Unit>` so you can include it in a select.

```csharp
async ValueTask<ChannelReader<Unit>> CreateTimeoutReaderAsync(ResultPipelineStepContext ctx, TimeSpan timeout, CancellationToken token)
{
    var ticker = ResultPipelineTimers.NewTicker(ctx, timeout, token);
    // Use the underlying channel to detect the first tick
    return ticker.Reader;
}
```

Stopping the ticker is handled by the registered compensation.

## Step 3 – Prepare the Work Case

Create a channel case that performs the actual enrichment. You can represent a single event as a channel of length 1 (or reuse an existing work channel).

```csharp
Channel<Unit> workChannel = Channel.CreateBounded<Unit>(1);
await workChannel.Writer.WriteAsync(Unit.Value, cancellationToken);
workChannel.Writer.TryComplete();
```

## Step 4 – Compose the Select Workflow

```csharp
var timeoutReader = await CreateTimeoutReaderAsync(context, TimeSpan.FromMilliseconds(150), cancellationToken);

var selectResult = await ResultPipelineChannels.Select<Unit>(context)
    .Case(workChannel.Reader, async (_, token) =>
    {
        var enrichment = await EnrichAsync(context, token);
        if (enrichment.IsFailure)
        {
            return enrichment.CastFailure<Unit>();
        }

        await _storage.WriteAsync(enrichment.Value, token);
        context.RegisterCompensation(async ct =>
        {
            await _storage.DeleteAsync(enrichment.Value.StorageKey, ct);
        });

        return Result.Ok(Unit.Value);
    })
    .Case(timeoutReader, priority: 0, (_, _) =>
    {
        var timeoutError = Error.Timeout(TimeSpan.FromMilliseconds(150))
            .WithMetadata("stage", "taxonomy");
        return Task.FromResult(Result.Fail<Unit>(timeoutError));
    })
    .ExecuteAsync();
```

The select builder automatically absorbs compensations (both for storage writes and timer disposal). If the timeout case fires first, the enrichment case is cancelled via the linked token.

## Step 5 – Handle Results and Replay Compensations

```csharp
if (selectResult.IsFailure)
{
    var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
    await Result.RunCompensationAsync(policy, scope, cancellationToken);
    _metrics.RecordTimeout(selectResult.Error!);
}
else
{
    _metrics.RecordSuccess();
}
```

## Optional: Combine with `ResultPipeline.WithTimeoutAsync`

For synchronous flows you can wrap the enrichment operation with `ResultPipeline.WithTimeoutAsync`, which internally uses the same timer helpers but with less boilerplate:

```csharp
var enrichResult = await ResultPipeline.WithTimeoutAsync(
    work: (ctx, token) => EnrichAsync(ctx, token),
    timeout: TimeSpan.FromMilliseconds(150),
    cancellationToken: cancellationToken);
```

If the timeout triggers, the returned `Result<T>` contains `Error.Timeout` and compensations registered by `EnrichAsync` (e.g., partial writes) are replayed.

## Summary

- `ResultPipelineTimers` provide deterministic timers tied to the pipeline’s time provider and cancellation token.
- `ResultPipelineChannels.Select<TResult>` lets you race between work and timeout branches while keeping compensations centralized.
- Register compensations for any stateful side effects (`_storage.DeleteAsync`, API call rollbacks) so that they run automatically when the timeout branch fires.
- The same pattern applies to SLA-based processing, backoff windows, or any deterministic deadline you need to enforce in the data plane.
