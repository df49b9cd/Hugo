# Result Pipelines

Hugo's `Result<T>` mirrors railway-oriented programming: values flow down the success track while failures short-circuit and capture structured errors.

## Creating Results

```csharp
using static Hugo.Go;

Result<int> ok = Ok(42);
Result<int> failure = Err<int>("validation failed", ErrorCodes.Validation);
```

- `Result.Ok` / `Result.Fail` construct instances directly.
- `Go.Ok` / `Go.Err` provide terse helpers for working with `using static Hugo.Go;`.
- Exceptions convert to `Error` with metadata via `Error.FromException`.

## Chaining Operations

```csharp
var result = Ok(request)
    .Ensure(r => !string.IsNullOrWhiteSpace(r.Email))
    .Then(SendEmail)
    .Map(response => response.MessageId)
    .Recover(err => Ok("fallback"));
```

- `Then` executes only on success; `Ensure` enforces invariants and returns validation errors when predicates fail.
- `Recover` and `RecoverAsync` switch tracks on failure, useful for default values or compensating actions.

## Async Composition

```csharp
var outcome = await Ok(input)
    .ThenAsync((value, ct) => validator.ValidateAsync(value, ct), ct)
    .MapAsync((value, ct) => transformer.TransformAsync(value, ct), ct)
    .TapAsync(async (value, ct) => await audit.LogAsync(value, ct), ct)
    .EnsureAsync((value, ct) => Task.FromResult(value.IsHealthy), ct)
    .FinallyAsync(
        onSuccess: value => value,
        onError: error => throw new InvalidOperationException(error.Message),
        cancellationToken: ct);
```

- Every async combinator honours `CancellationToken` and converts cancellations to `Error.Canceled`, including the originating token in the error metadata for downstream inspection.
- `FinallyAsync` picks the branch to project, enabling deterministic control flow around result pipelines.

## Error Metadata

Attach diagnostic context to errors for richer observability.

```csharp
var result = Ok(user)
    .Ensure(
        predicate: u => u.Age >= 18,
        errorFactory: u => Error.From("age must be >= 18", ErrorCodes.Validation)
            .WithMetadata("age", u.Age)
            .WithMetadata("userId", u.Id));

if (result.IsFailure)
{
    if (result.Error!.TryGetMetadata<int>("age", out var age))
    {
        logger.LogWarning("Rejected under-age user {UserId} ({Age})", result.Error.Metadata["userId"], age);
    }
}
```

Metadata flows downstream through `Recover`, `TapError`, and `Finally`, making it easy to bubble context into logs or metrics.
