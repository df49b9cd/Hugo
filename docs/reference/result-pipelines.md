# Result Pipeline Reference

`Result<T>` is the primary abstraction for representing success or failure. This reference lists factory helpers, synchronous combinators, async extensions, and error metadata APIs.

## Creating results

- `Result.Ok()` / `Result.Ok<T>(T value)`
- `Result.Fail(Error error)` / `Result.Fail<T>(Error error)`
- `Go.Ok(value)` / `Go.Err<T>(message, code)`
- `Result.Try(Func<T>)` / `Result.TryAsync(Func<CancellationToken, Task<T>>)`

### Creation example

```csharp
var ok = Go.Ok(42);
var failure = Go.Err<int>("validation failed", ErrorCodes.Validation);
var fromTask = await Result.TryAsync(async ct => await repository.LoadAsync(id, ct), ct);
```

## Inspecting state

- `result.IsSuccess` / `result.IsFailure`
- `result.Value` (throws if failure)
- `result.Error`

## Synchronous combinators

- `Then(Func<T, Result<TResult>>)`
- `Map(Func<T, TResult>)`
- `Tap(Action<T>)`
- `Ensure(Func<T, bool>, Func<T, Error>? errorFactory = null)`
- `Recover(Func<Error, Result<T>>)`
- `Finally<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onError)`

### Synchronous pipeline example

```csharp
var result = Go.Ok(request)
    .Ensure(r => !string.IsNullOrWhiteSpace(r.Email))
    .Then(SendEmail)
    .Map(response => response.MessageId)
    .Recover(err => Go.Ok("fallback"));
```

## Async combinators

Each async method accepts the current value and a `CancellationToken`.

- `ThenAsync(Func<T, CancellationToken, Task<Result<TResult>>>, CancellationToken)`
- `MapAsync(Func<T, CancellationToken, Task<TResult>>, CancellationToken)`
- `TapAsync(Func<T, CancellationToken, Task>, CancellationToken)`
- `EnsureAsync(Func<T, CancellationToken, Task<bool>>, CancellationToken)`
- `RecoverAsync(Func<Error, CancellationToken, Task<Result<T>>>, CancellationToken)`
- `FinallyAsync<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onError, CancellationToken)`

## Retry and fallback orchestration

- `Result.RetryWithPolicyAsync` executes an operation under a `ResultExecutionPolicy`, applying retries and compensation before surfacing a failure.
- `Result.TieredFallbackAsync` evaluates `ResultFallbackTier<T>` instances sequentially. Within each tier, multiple strategies run concurrently and cancel once a peer succeeds. Failures accumulate `fallbackTier`, `tierIndex`, and `strategyIndex` metadata for observability.
- `ResultFallbackTier<T>.From` converts synchronous or asynchronous delegates into tier definitions without manual `ResultPipelineStepContext` handling.
- `ErrGroup.Go((ctx, ct) => ..., policy: retryPolicy)` fans out result-aware work inside an errgroup. The overload honours retry policies and executes registered compensation when a step fails.

### Tiered fallback example

```csharp
var policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200)));

var tiers = new[]
{
    ResultFallbackTier<HttpResponseMessage>.From(
        "primary",
        ct => TrySendAsync(primaryClient, payload, ct)),
    new ResultFallbackTier<HttpResponseMessage>(
        "regional",
        new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<HttpResponseMessage>>>[]
        {
            (ctx, ct) => TrySendAsync(euClient, payload, ct),
            (ctx, ct) => TrySendAsync(apacClient, payload, ct)
        })
};

var response = await Result.TieredFallbackAsync(tiers, policy, cancellationToken);

if (response.IsFailure && response.Error!.Metadata.TryGetValue("fallbackTier", out var tier))
{
    logger.LogWarning("All strategies in tier {Tier} failed: {Error}", tier, response.Error);
}
```

### ErrGroup integration

```csharp
using var group = new ErrGroup();
var retryPolicy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(3, TimeSpan.FromSeconds(1)));

group.Go((ctx, ct) =>
{
    return Result.RetryWithPolicyAsync(async (_, token) =>
    {
        var response = await client.SendAsync(request, token);
        return response.IsSuccessStatusCode
            ? Result.Ok(Go.Unit.Value)
            : Result.Fail<Unit>(Error.From("HTTP failure", ErrorCodes.Validation));
    }, retryPolicy, ct, ctx.TimeProvider);
}, stepName: "ship-order", policy: retryPolicy);

var completion = await group.WaitAsync(cancellationToken);
```

### Metadata example

```csharp
var outcome = await Go.Ok(input)
    .ThenAsync((value, ct) => validator.ValidateAsync(value, ct), ct)
    .MapAsync((value, ct) => transformer.TransformAsync(value, ct), ct)
    .TapAsync(async (value, ct) => await audit.LogAsync(value, ct), ct)
    .EnsureAsync((value, ct) => Task.FromResult(value.IsHealthy), ct)
    .FinallyAsync(
        onSuccess: value => value,
        onError: error => throw new InvalidOperationException(error.Message),
        cancellationToken: ct);
```

## Error metadata

- `Error.WithMetadata(string key, object value)`
- `Error.TryGetMetadata<T>(string key, out T value)`
- `Error.Metadata` (read-only dictionary)

### Example

```csharp
var result = Go.Ok(user)
    .Ensure(
        predicate: u => u.Age >= 18,
        errorFactory: u => Error.From("age must be >= 18", ErrorCodes.Validation)
            .WithMetadata("age", u.Age)
            .WithMetadata("userId", u.Id));

if (result.IsFailure && result.Error!.TryGetMetadata<int>("age", out var age))
{
    logger.LogWarning("Rejected under-age user {UserId} ({Age})", result.Error.Metadata["userId"], age);
}
```

## Cancellation handling

- `Error.Canceled` represents cancellation captured via Hugo APIs.
- Async combinators convert `OperationCanceledException` into `Error.Canceled` and attach the triggering token in metadata under `"cancellationToken"`.

## Diagnostics

When `GoDiagnostics` is configured, result creation increments:

- `result.successes`
- `result.failures`

`TapError`-style hooks contribute to `result.failures` as well, making it easy to correlate pipelines with observability platforms.
