# Flat-Mapping Result Streams

When streaming pipelines need to expand a single input into multiple derived outputs (fan-out-in-the-middle), you can combine `Result.MapStreamAsync`, channels, and `ResultPipelineChannels.MergeAsync` to flat-map results without leaving the Result pipeline. This tutorial walks through a scenario where each incoming record spawns several derived metrics.

## Scenario

You ingest `SensorEnvelope` events. Each envelope contains raw readings for multiple sub-sensors. For every envelope:

1. Validate and normalize the envelope.
2. Generate one derived `Metric` per sub-sensor.
3. Emit all metrics downstream as part of the same Result pipeline.

## Strategy

- Use `Result.MapStreamAsync` to convert the ingress `IAsyncEnumerable<SensorEnvelope>` into `Result<ChannelReader<Metric>>`, where each reader contains the derived metrics for a single envelope.
- Use `ResultPipelineChannels.MergeAsync` to merge the per-envelope channels into a single stream.
- Continue processing the merged channel with the usual Result combinators.

## Step 1 – Normalize Envelopes Into Channels

```csharp
IAsyncEnumerable<SensorEnvelope> ingress = _sensorHub.ReadAllAsync(ct);

var perEnvelopeChannels = Result.MapStreamAsync(ingress, async (envelope, token) =>
{
    var normalized = await NormalizeAsync(envelope, token);
    if (normalized.IsFailure)
    {
        return normalized.CastFailure<ChannelReader<Metric>>();
    }

    var channel = Channel.CreateUnbounded<Metric>();
    _ = Task.Run(async () =>
    {
        try
        {
            foreach (var metric in ExpandMetrics(normalized.Value))
            {
                await channel.Writer.WriteAsync(metric, token);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }, token);

    return Result.Ok(channel.Reader);
}, ct);
```

Each envelope yields a channel reader. Failures are propagated as `Result.Fail` and short-circuit the stream.

## Step 2 – Merge Derived Metrics Back Into One Stream

```csharp
Channel<Metric> merged = Channel.CreateUnbounded<Metric>();

var mergeResult = await perEnvelopeChannels
    .ForEachAsync((result, token) =>
    {
        if (result.IsFailure)
        {
            return new ValueTask<Result<Unit>>(result.CastFailure<Unit>());
        }

        return new ValueTask<Result<Unit>>(ResultPipelineChannels.MergeAsync(
            context,
            new[] { result.Value },
            merged.Writer,
            completeDestination: false,
            cancellationToken: token));
    },
    ct);
```

- When the iteration finishes successfully, `merged.Reader` exposes a continuous stream of `Metric` instances regardless of how many each envelope produced.
- If any envelope fails normalization, `mergeResult` returns failure and you can replay compensations as usual.

## Step 3 – Continue Downstream

```csharp
await merged.Reader
    .ReadAllAsync(ct)
    .Select(metric => Result.Ok(metric))
    .ForEachAsync(async (metricResult, token) =>
    {
        if (metricResult.IsFailure)
        {
            await Result.RunCompensationAsync(_policy, scope, token);
            return metricResult.CastFailure<Unit>();
        }

        await _metricSink.WriteAsync(metricResult.Value, token);
        return Result.Ok(Go.Unit.Value);
    },
    ct);
```

## Summary

- Treat each envelope as a mini stream by projecting to `ChannelReader<T>`.
- Use `ResultPipelineChannels.MergeAsync` (or repeated merges) to flatten the channels back into one stream.
- Because the entire flow stays inside `Result<T>`, compensations and cancellations propagate naturally.
