# Emitting Telemetry for Every Streaming Result

`Result.ForEachAsync` makes it easy to perform per-item side effects (logging, metrics) while keeping the pipeline fluent. This tutorial covers two common patterns:

1. Emitting metrics for every success (`TapSuccessEach`).
2. Recording errors without stopping the stream (`TapFailureEach`).

## Base Pattern

```csharp
await sourceStream
    .ForEachAsync(async (result, token) =>
    {
        if (result.IsFailure)
        {
            await _metrics.RecordFailureAsync(result.Error!, token);
            return Result.Ok(Go.Unit.Value); // keep processing
        }

        await _metrics.RecordSuccessAsync(result.Value, token);
        return Result.Ok(Go.Unit.Value);
    },
    cancellationToken);
```

This pattern already works for both success and failure cases, but Hugo now ships dedicated helpers to keep call sites tidy.

## Tap Only Successes

```csharp
await ingress
    .TapSuccessEachAsync(async (record, token) =>
    {
        _logger.LogInformation("processed {RecordId}", record.Id);
        await _metrics.RecordSuccessAsync(record.Id, token);
    }, ct);
```

## Tap Only Failures

```csharp
await ingress
    .TapFailureEachAsync(async (error, token) =>
    {
        _logger.LogWarning("filtered record: {Message}", error.Message);
        await _metrics.RecordFailureAsync(error.Code ?? ErrorCodes.Unspecified, token);
    }, ct);
```

## Usage Example

```csharp
await Result.MapStreamAsync(ingress, ValidateAsync, ct)
    .TapSuccessEachAsync((record, token) =>
    {
        _successCounter.Increment();
        return ValueTask.CompletedTask;
    }, ct)
    .TapFailureEachAsync((error, token) =>
    {
        _failureCounter.Add(error.Message);
        return ValueTask.CompletedTask;
    }, ct);
```

## Summary

- `Result.ForEachAsync` is the foundation for per-item side effects, while `TapSuccessEachAsync`/`TapFailureEachAsync` expose the most common cases directly.
- Both helpers walk the entire stream so all side effects run, but they return a failure result if any item in the stream failed (using the first error they observed).
- Because these helpers themselves return `Result<Unit>`, you can chain them with other combinators or short-circuit on failure when appropriate.
- Prefer the aggregate variants (`TapSuccessEachAggregateErrorsAsync` / `TapFailureEachAggregateErrorsAsync`) when you need visibility into every failure, and the ignore variants (`TapSuccessEachIgnoreErrorsAsync` / `TapFailureEachIgnoreErrorsAsync`) when the caller should keep flowing even if the stream contained errors.
