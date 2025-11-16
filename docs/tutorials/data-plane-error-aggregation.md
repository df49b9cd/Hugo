# Aggregating Errors in Streaming Pipelines

Most streaming combinators short-circuit on the first failure. Sometimes you need to collect all failures for diagnostics while still returning a `Result<T>` at the end. This tutorial shows how to aggregate errors without leaving the Result pipeline.

## Scenario

- You accept an `IAsyncEnumerable<Result<Record>>`.
- You want to persist all successful records but also capture every failure for reporting.
- The pipeline should still return a single `Result<Unit>`: success if all records persisted, failure containing an aggregated error otherwise.

## Implementation

Use `Result.CollectErrorsAsync` to gather successes and aggregate failures automatically:

```csharp
var aggregation = await ingressStream.CollectErrorsAsync(ct);
if (aggregation.IsFailure)
{
    return aggregation.CastFailure<Unit>();
}

foreach (var record in aggregation.Value)
{
    var persisted = await _repository.SaveAsync(record, ct);
    if (persisted.IsFailure)
    {
        return persisted.CastFailure<Unit>();
    }
}

return Result.Ok(Unit.Value);
```

## Notes

- The helper returns either a success containing the accumulated values or a single aggregated `Error`.
- Because it never throws, you remain inside the Result pipeline and can continue chaining combinators (`TapFailureEachAsync` for logging, etc.).

## Summary

- `Result.CollectErrorsAsync` consumes the stream, keeps all successful values, and collapses failures into one `Error` (using `Error.Aggregate` when necessary).
- After aggregation, you can continue processing or bubble up the combined failure while still replaying compensations as usual.
