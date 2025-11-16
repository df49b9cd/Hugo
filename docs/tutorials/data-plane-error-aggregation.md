# Aggregating Errors in Streaming Pipelines

Most streaming combinators short-circuit on the first failure. Sometimes you need to collect all failures for diagnostics while still returning a `Result<T>` at the end. This tutorial shows how to aggregate errors without leaving the Result pipeline.

## Scenario

- You accept an `IAsyncEnumerable<Result<Record>>`.
- You want to persist all successful records but also capture every failure for reporting.
- The pipeline should still return a single `Result<Unit>`: success if all records persisted, failure containing an aggregated error otherwise.

## Implementation

Use `ForEachAsync` with an error bag:

```csharp
var errors = new List<Error>();

var finalResult = await ingressStream.ForEachAsync(async (result, token) =>
{
    if (result.IsFailure)
    {
        errors.Add(result.Error ?? Error.Unspecified());
        return Result.Ok(Go.Unit.Value); // keep processing
    }

    await _repository.SaveAsync(result.Value, token);
    return Result.Ok(Go.Unit.Value);
}, ct);

if (errors.Count > 0)
{
    var aggregate = errors.Count == 1
        ? errors[0]
        : Error.Aggregate("One or more records failed", errors);

    return Result.Fail<Unit>(aggregate);
}

return finalResult;
```

## Notes

- `ForEachAsync` keeps consuming the stream even when individual records fail.
- After the iteration, you decide whether to return success or a combined failure.
- You can enrich each error with metadata (e.g., record IDs) before adding it to the list.

## Summary

- Aggregating errors is a matter of collecting them during `ForEachAsync` and returning a single `Result.Fail` at the end.
- The approach preserves Result semantics (you can still replay compensations or short-circuit early by returning failure from the callback).
