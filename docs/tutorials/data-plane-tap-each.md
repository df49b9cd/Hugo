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

This pattern already works for both success and failure cases, but you can wrap it in extension methods to keep call sites tidy.

## Tap Only Successes

```csharp
public static ValueTask<Result<Unit>> TapSuccessEachAsync<T>(
    this IAsyncEnumerable<Result<T>> source,
    Func<T, CancellationToken, ValueTask> tap,
    CancellationToken cancellationToken = default) =>
    source.ForEachAsync(async (result, token) =>
    {
        if (result.IsSuccess)
        {
            await tap(result.Value, token);
        }

        return Result.Ok(Go.Unit.Value);
    }, cancellationToken);
```

## Tap Only Failures

```csharp
public static ValueTask<Result<Unit>> TapFailureEachAsync<T>(
    this IAsyncEnumerable<Result<T>> source,
    Func<Error, CancellationToken, ValueTask> tap,
    CancellationToken cancellationToken = default) =>
    source.ForEachAsync(async (result, token) =>
    {
        if (result.IsFailure && result.Error is Error error)
        {
            await tap(error, token);
        }

        return Result.Ok(Go.Unit.Value);
    }, cancellationToken);
```

## Usage Example

```csharp
await Result.MapStreamAsync(ingress, ValidateAsync, ct)
    .TapSuccessEachAsync(async (record, token) =>
    {
        _logger.LogInformation("processed {RecordId}", record.Id);
        await Task.CompletedTask;
    }, ct)
    .TapFailureEachAsync(async (error, token) =>
    {
        _logger.LogWarning("skipped: {Message}", error.Message);
        await Task.CompletedTask;
    }, ct);
```

## Summary

- `Result.ForEachAsync` is the foundation for per-item side effects.
- Wrap common tap patterns in helpers to keep pipelines readable.
- Because these helpers themselves return `Result<Unit>`, you can chain them with other combinators or short-circuit on failure when appropriate.
