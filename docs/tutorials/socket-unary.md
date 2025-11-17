# Unary Socket Pipelines

Unary socket calls (open → send one request → read one reply → close) still benefit from Hugo’s Result pipeline when you need deterministic retries, compensation, and telemetry. This guide shows how to wrap a unary request/response around `ResultPipeline.RetryAsync`, `ResultPipeline.WithTimeoutAsync`, and functional Result chains.

## Scenario

You must push config updates to a device over a TCP socket. Each call:

1. Connects, sends a payload, waits for a reply.
2. Retries twice with exponential backoff.
3. Rolls back the “config staged” flag if the request fails or times out.

## Architecture

```
ResultPipeline.RetryAsync
        │
        ▼
Result.TryAsync(connect/send/read)
        │
        ├─ WithTimeoutAsync (per attempt)
        └─ Compensation (clear staged flag)
```

## Implementation

```csharp
async ValueTask<Result<Unit>> PushConfigAsync(
    DeviceConfig config,
    ResultPipelineStepContext ctx,
    CancellationToken cancellationToken)
{
    return await ResultPipeline.RetryAsync(async (stepCtx, token) =>
    {
        stepCtx.RegisterCompensation(_ => _deviceRegistry.ClearStagedAsync(config.DeviceId, token));

        return await Result.WithTimeoutAsync(
            async innerToken =>
            {
                using var socket = await _socketFactory.ConnectAsync(config.Endpoint, innerToken);
                await socket.WriteAsync(config.Payload, innerToken);

                var reply = await socket.ReadAsync(innerToken);
                return reply.IsAck ? Result.Ok(Unit.Value) : Result.Fail<Unit>(Error.From("nack"));
            },
            TimeSpan.FromSeconds(5),
            cancellationToken: token);
    },
    policy: ResultExecutionPolicy.ExponentialRetry(maxAttempts: 3, TimeSpan.FromMilliseconds(250)),
    cancellationToken: cancellationToken);
}
```

## Functional Chaining

Because everything returns `Result<T>` you can add transformations before/after the socket call:

```csharp
await Result
    .Try(() => Validate(config))
    .ThenAsync(_ => PushConfigAsync(config, ctx, ct))
    .TapFailure(error => _metrics.RecordUnaryFailure(config.DeviceId, error.Code));
```

## Heartbeats & Observability

- Record per-attempt metrics: wrap the inner lambda with `TapSuccessEachAsync` / `TapFailureEachAsync` (or the aggregate/ignore variants when you need all failures reported or telemetry-only behavior).
- Emit heartbeat pings before the real payload by inserting a `socket.WriteAsync(heartbeat)` call guarded by `Result.PipelineTimers.NewTicker` if devices expect “keepalive” frames.

## HttpClient Example

Unary HTTP calls map directly to the same pipeline. Wrap `HttpClient` send/receive in `Result.TryAsync`, add retries/timeouts, and attach compensations for any state staged before the request leaves the process.

```csharp
async ValueTask<Result<HttpResponseMessage>> InvokeHttpAsync(
    HttpRequestMessage request,
    ResultPipelineStepContext ctx,
    CancellationToken cancellationToken)
{
    ctx.RegisterCompensation(_ => _audit.LogAsync("http-rollback", cancellationToken));

    return await ResultPipeline.RetryAsync(
        (_, token) => Result.WithTimeoutAsync(
            async innerToken =>
            {
                using var response = await _httpClient.SendAsync(request, innerToken);
                if (!response.IsSuccessStatusCode)
                {
                    return Result.Fail<HttpResponseMessage>(Error.From($"http {response.StatusCode}", ErrorCodes.Exception));
                }

                return Result.Ok(response);
            },
            TimeSpan.FromSeconds(3),
            cancellationToken: token),
        policy: ResultExecutionPolicy.ExponentialRetry(3, TimeSpan.FromMilliseconds(200)),
        cancellationToken: cancellationToken);
}
```

- **Functional chain:** `ValidateRequest().ThenAsync(_ => InvokeHttpAsync(...)).TapFailure(logError)` keeps validation, transport, and post-processing unified.
- **Compensation:** Attach rollback handlers for cache entries, staged commands, or feature flags toggled prior to the HTTP call.
- **Telemetry:** Use `TapSuccessEachAsync` to increment per-endpoint counters and `TapFailureEachAsync` to emit structured logs for failed status codes. For non-blocking metrics, swap in the `IgnoreErrors` variants; for SLA rollups, prefer the `AggregateErrors` variants.

## When to Use

Choose this pattern whenever a unary socket call needs retries, compensation, or structured errors. For high-volume fire-and-forget traffic, see the one-way tutorial; for continuous streams, use the duplex or streaming guides. 
