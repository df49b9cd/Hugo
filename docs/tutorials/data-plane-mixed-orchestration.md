# Mixed Orchestration & Streaming Pipelines

This tutorial demonstrates how to combine list-based orchestration (`ResultPipeline.FanOutAsync`, `Result.WhenAll`) with streaming/channel-based data planes (`ResultPipelineChannels.MergeAsync`, `Result.MapStreamAsync`). The result is an end-to-end flow where you can orchestrate deterministic fan-out stages and then continue processing as an `IAsyncEnumerable<Result<T>>` without losing compensation tracking.

## Scenario

You need to process batches of files:

1. Fan out a list of extraction jobs (one per file).
2. Each job streams records to a channel.
3. Downstream components merge all job streams into a single channel, apply validations, and persist them.
4. Failures anywhere should roll back compensations recorded in either the orchestration or streaming stages.

## Step 1 – Orchestrate Extraction Jobs With `ResultPipeline.FanOutAsync`

Create a list of extraction operations. Each operation returns `Result<ExtractionHandle>` where the handle exposes a `ChannelReader<Record>` and compensations for temporary resources.

```csharp
IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<ExtractionHandle>>>> operations =
    batch.Files.Select(file => (Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<ExtractionHandle>>>)
        (async (ctx, token) =>
        {
            var tempPath = await _extractor.StartAsync(file, token);
            ctx.RegisterCompensation(async ct => await _extractor.CleanupAsync(tempPath, ct));

            return new ExtractionHandle(file.Id, _extractor.GetChannel(file.Id));
        }));

var fanOutResult = await ResultPipeline.FanOutAsync(operations, policy: _retryPolicy, cancellationToken: cancellationToken);
if (fanOutResult.IsFailure)
{
    await Result.RunCompensationAsync(_retryPolicy, scope, cancellationToken);
    return fanOutResult.CastFailure<Unit>();
}
```

`fanOutResult.Value` is an `IReadOnlyList<ExtractionHandle>`. The compensation scope now contains per-file cleanup actions.

## Step 2 – Merge Job Streams Into a Single Channel

Use `ResultPipelineChannels.MergeAsync` to take the record readers from each handle and merge them into a single destination channel.

```csharp
Channel<Record> mergedChannel = Channel.CreateUnbounded<Record>();

var mergeResult = await ResultPipelineChannels.MergeAsync(
    context,
    fanOutResult.Value.Select(handle => handle.Reader),
    mergedChannel.Writer,
    completeDestination: true,
    cancellationToken: cancellationToken);

if (mergeResult.IsFailure)
{
    await Result.RunCompensationAsync(_retryPolicy, scope, cancellationToken);
    return mergeResult.CastFailure<Unit>();
}
```

`mergeResult` returns once all extraction handles complete or an error surfaces. The writer is automatically completed.

## Step 3 – Process the Merged Stream With `Result.MapStreamAsync`

Convert the merged channel to an async stream of `Result<Record>` and apply functional combinators.

```csharp
var recordStream = mergedChannel.Reader
    .ReadAllAsync(cancellationToken)
    .Select(record => Result.Ok(record));

await foreach (var result in Result.MapStreamAsync(
    recordStream,
    async (record, ct) =>
    {
        await _validator.EnsureValidAsync(record, ct);
        return Result.Ok(record);
    },
    cancellationToken))
{
    if (result.IsFailure)
    {
        await Result.RunCompensationAsync(_retryPolicy, scope, cancellationToken);
        yield break;
    }

    await _repository.SaveAsync(result.Value, cancellationToken);
}
```

Because each stage returns `Result<T>`, downstream consumers can stop as soon as the first failure appears.

## Step 4 – Finalize and Replay Compensations if Needed

If the entire pipeline completes successfully, the compensation scope should be empty (every extraction handle cleared its cleanup). If a failure occurred at any point, `Result.RunCompensationAsync` replays actions from both the orchestration phase (file cleanup) and streaming phase (e.g., deleting persisted records).

## Summary

- Use list-based orchestrators (`ResultPipeline.FanOutAsync`, `Result.WhenAll`) for deterministic fan-out/discrete stages.
- Switch to streaming/channel adapters (`ResultPipelineChannels.MergeAsync`, `Result.MapStreamAsync`) once you need to process unbounded data.
- Both halves share the same `ResultPipelineStepContext`, so compensations recorded in either stage run if the pipeline fails.
- The `Result<T>` surface lets you interleave synchronous combinators (`ThenAsync`, `TapAsync`) with streaming combinators (`MapStreamAsync`, `ReadAllAsync`) without leaving the Result pipeline.
