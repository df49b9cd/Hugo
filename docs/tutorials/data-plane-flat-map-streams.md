# Flat-Mapping Result Streams

When streaming pipelines need to expand a single input into multiple derived outputs (fan-out-in-the-middle), `Result.FlatMapStreamAsync` lets you describe the fan-out without allocating intermediate channels. This tutorial walks through a scenario where each incoming record spawns several derived metrics.

## Scenario

You ingest `SensorEnvelope` events. Each envelope contains raw readings for multiple sub-sensors. For every envelope:

1. Validate and normalize the envelope.
2. Generate one derived `Metric` per sub-sensor.
3. Emit all metrics downstream as part of the same Result pipeline.

## Step 1 – Normalize and Expand With `FlatMapStreamAsync`

```csharp
IAsyncEnumerable<SensorEnvelope> ingress = _sensorHub.ReadAllAsync(ct);

IAsyncEnumerable<Result<Metric>> metricStream = Result.FlatMapStreamAsync(
    ingress,
    (envelope, token) => ExpandEnvelopeAsync(envelope, token),
    ct);

static async IAsyncEnumerable<Result<Metric>> ExpandEnvelopeAsync(
    SensorEnvelope envelope,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    var normalized = await NormalizeAsync(envelope, cancellationToken);
    if (normalized.IsFailure)
    {
        yield return normalized.CastFailure<Metric>();
        yield break;
    }

    foreach (var metric in ExpandMetrics(normalized.Value))
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return Result.Ok(metric);
    }
}
```

- The selector returns an `IAsyncEnumerable<Result<Metric>>`. Successes stream downstream immediately; failures short-circuit the outer stream.
- No intermediate channels are needed—the helper handles the select-many logic and cancellation wiring.

## Step 2 – Consume the Flattened Stream

```csharp
await metricStream.ForEachAsync(async (result, token) =>
{
    if (result.IsFailure)
    {
        await Result.RunCompensationAsync(_policy, scope, token);
        return result.CastFailure<Unit>();
    }

    await _metricSink.WriteAsync(result.Value, token);
    return Result.Ok(Unit.Value);
}, ct);
```

## Summary

- Treat each envelope as a mini stream by returning an async enumerable of `Result<Metric>` from the selector.
- `Result.FlatMapStreamAsync` flattens all derived metrics deterministically, automatically halting on the first failure.
- Because the entire flow stays inside `Result<T>`, compensations and cancellations propagate naturally.
