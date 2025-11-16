# Filtering Streaming Results

Sometimes a data-plane stage needs to drop certain records (e.g., bad telemetry, feature flags) while letting the rest flow downstream without breaking the Result pipeline. This tutorial shows how to implement streaming filters using `Result.FilterStreamAsync` and `ForEachAsync`.

## Scenario

You receive `Result<Record>` instances from an upstream validator. You now need to:

- Discard records that don’t meet business rules (e.g., `record.Region == "EU"`).
- Log discarded records for auditing.
- Continue processing the remaining records seamlessly.

## Approach

1. Convert the upstream channel to `IAsyncEnumerable<Result<Record>>`.
2. Use `Result.FilterStreamAsync` to drop records that fail business rules while keeping failures untouched.
3. Use `ForEachAsync` to consume the filtered stream.

## Step 1 – Stream Ingress Results

```csharp
IAsyncEnumerable<Result<Record>> ingressResults = _validator
    .ReadAllAsync(ct)
    .Select(payload => Validate(payload));
```

`Validate` returns `Result<Record>`.

## Step 2 – Apply Filtering Rules

```csharp
var filteredStream = Result.FilterStreamAsync(
    ingressResults,
    record =>
    {
        var keep = record.Region != "EU";
        if (!keep)
        {
            _logger.LogInformation("filtered EU record {RecordId}", record.Id);
        }

        return keep;
    },
    ct);
```

- Successful records that return `true` continue downstream unchanged.
- Successful records that return `false` are dropped from the stream without introducing failures.
- Any existing failures from upstream continue flowing so you can distinguish business-rule drops from genuine errors.

## Step 3 – Consume the Filtered Stream

```csharp
await filteredStream.ForEachAsync(async (result, token) =>
{
    if (result.IsFailure)
    {
        // Skip filtered items – nothing else to do because we already logged them.
        return Result.Ok(Go.Unit.Value);
    }

    await _downstreamSink.WriteAsync(result.Value, token);
    return Result.Ok(Go.Unit.Value);
}, ct);
```

- Failures representing filtered records do not stop the stream—`ForEachAsync` simply ignores them by returning success.
- If you want true errors to stop the pipeline, adjust the conditional (e.g., check `error.Code`).

## Summary

- `Result.FilterStreamAsync` drops successful records that fail your predicate while leaving failure results untouched for diagnostics.
- Use synchronous logging inside the predicate or combine with `TapFailureEachAsync` / `TapSuccessEachAsync` to emit telemetry.
- `ForEachAsync` provides a fluent way to consume the resulting stream without `await foreach`.
- Because the flow keeps using `Result<T>`, compensations and diagnostics remain consistent.
