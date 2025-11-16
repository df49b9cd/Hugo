# Filtering Streaming Results

Sometimes a data-plane stage needs to drop certain records (e.g., bad telemetry, feature flags) while letting the rest flow downstream without breaking the Result pipeline. This tutorial shows how to implement streaming filters using `Result.MapStreamAsync`, `Ensure`, and `ForEachAsync`.

## Scenario

You receive `Result<Record>` instances from an upstream validator. You now need to:

- Discard records that don’t meet business rules (e.g., `record.Region == "EU"`).
- Log discarded records for auditing.
- Continue processing the remaining records seamlessly.

## Approach

1. Convert the upstream channel to `IAsyncEnumerable<Result<Record>>`.
2. Use `Result.MapStreamAsync` to apply business rules (`Ensure`, `TapError`).
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
var filteredStream = Result.MapStreamAsync(
    ingressResults,
    (result, token) => result
        .Ensure(static record => record.Region != "EU", record =>
            Error.From($"EU traffic disabled for {record.Id}", "error.filtered"))
        .TapError(static error =>
        {
            Console.WriteLine($"filtered: {error.Message}");
            return ValueTask.CompletedTask;
        }),
    cancellationToken: ct);
```

Here you reuse the existing Result combinators:

- `Ensure` returns a failure result if the predicate is false.
- `TapError` logs filtered items without altering the failure.

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

- Streaming filters can be built with existing combinators: `Ensure` to keep/drop items, `TapError`/`TapAsync` for logging.
- `ForEachAsync` provides a fluent way to consume the stream without `await foreach`.
- Because the flow keeps using `Result<T>`, compensations and diagnostics remain consistent.
