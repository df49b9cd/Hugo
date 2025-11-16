# Streaming Socket Pipelines

Long-lived streaming sockets (log tailing, IoT telemetry, chat rooms) are best modeled as continuous `IAsyncEnumerable<Result<T>>` pipelines with backpressure, filtering, error aggregation, and compensation. This tutorial combines Hugo’s streaming helpers to keep such flows deterministic.

## Scenario

- A gateway streams millions of events per minute.
- You must fan the stream out to multiple downstream processors.
- Each processor enforces per-tenant throttles, heartbeats, and retryable writes to storage.
- Failures shouldn’t stop the stream; aggregate errors for periodic reporting.

## Architecture

```
Socket Reader → Result.MapStreamAsync (frames → Result<Event>)
      │
      ├─ Result.FilterStreamAsync (drop muted tenants)
      ├─ ResultPipelineChannels.FanOut (per-tenant branches)
      └─ Per branch:
             ResultPipelineChannels.WindowAsync (batch N or 1s)
             ResultPipeline.RetryAsync (persist batch)
             TapSuccessEachAsync / TapFailureEachAsync (metrics)
             CollectErrorsAsync (report)
```

## Stream Ingestion

```csharp
IAsyncEnumerable<Result<Event>> ingress = Result.MapStreamAsync(
    _socket.ReadFramesAsync(ct),
    async (frame, token) =>
    {
        var parsed = await _parser.ParseAsync(frame, token);
        return parsed.IsSuccess ? Result.Ok(parsed.Value) : Result.Fail<Event>(parsed.Error);
    },
    ct);
```

## Filtering & Fan-Out

```csharp
var filtered = Result.FilterStreamAsync(
    ingress,
    evt => !_muteList.Contains(evt.TenantId),
    ct);

var hub = Channel.CreateUnbounded<Result<Event>>();
_ = filtered.ToChannelAsync(hub.Writer, ct);

var branches = ResultPipelineChannels.FanOut(
    ctx,
    hub.Reader,
    branchCount: _tenantCount,
    completeBranches: true,
    cancellationToken: ct);
```

## Per-Tenant Processing

```csharp
foreach (var branch in branches)
{
    _ = ProcessTenantAsync(branch, tenantCtx, ct);
}

async Task ProcessTenantAsync(ChannelReader<Result<Event>> reader, ResultPipelineStepContext tenantCtx, CancellationToken token)
{
    await ResultPipelineChannels
        .WindowAsync(tenantCtx, reader, batchSize: 500, flushInterval: TimeSpan.FromSeconds(1), token)
        .ReadAllAsync(token)
        .ForEachAsync(async (batchResult, ct2) =>
        {
            if (batchResult.IsFailure)
            {
                return Result.Ok(Unit.Value); // skip bad records
            }

            var retry = await ResultPipeline.RetryAsync(
                (_, innerToken) => _storage.WriteBatchAsync(batchResult.Value, innerToken),
                policy: ResultExecutionPolicy.ExponentialRetry(5, TimeSpan.FromMilliseconds(200)),
                cancellationToken: ct2);

            return retry;
        }, token);
}
```

## Heartbeats & Backpressure

- Use `ResultPipelineTimers.NewTicker` per tenant to send heartbeat events downstream or to detect idle partitions.
- If storage slows down, backpressure automatically propagates because channel writes await capacity; combine with `ResultPipelineChannels.MergeWithStrategyAsync` to prioritize critical tenants under load.

## Error Aggregation

```csharp
var errorReport = await filtered
    .CollectErrorsAsync(ct)
    .TapFailure(error => _errorStore.RecordAggregate(error));
```

## HttpClient Streaming Example

Many services expose long-lived HTTP streams (Server-Sent Events, log tailing, AI responses). You can plug `HttpClient` into the same pipeline by treating `HttpClient.GetStreamAsync` as the socket reader:

```csharp
async IAsyncEnumerable<Result<Event>> ReadSseAsync(
    Uri endpoint,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    var stream = await _httpClient.GetStreamAsync(endpoint, cancellationToken);
    using var reader = new StreamReader(stream);

    while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        yield return Result.Try(() => _sseParser.Parse(line));
    }
}

var ssePipeline = Result.FlatMapStreamAsync(
    ReadSseAsync(_feedEndpoint, ct),
    async (message, token) =>
    {
        await _heartbeat.UpdateAsync("sse-feed", token);
        yield return message;
    },
    ct);
```

- Reuse the same fan-out/batching sections above to process SSE payloads.
- Wrap the SSE reader with `ResultPipeline.RetryAsync` + `ResultPipeline.WithTimeoutAsync` to reconnect when the server closes the stream.
- Attach compensations to undo downstream effects whenever the HTTP channel resets unexpectedly.

## Functional Chains

You can still chain post-processing steps:

```csharp
await ingress
    .TapSuccessEachAsync((evt, token) => _metrics.RecordIngress(evt.TenantId), ct)
    .TapFailureEachAsync((err, token) => _metrics.RecordIngressFailure(err.Code), ct);
```

## gRPC Server Streaming Handler

Server-side gRPC handlers can reuse the same streaming constructs. Each incoming call yields an `IAsyncEnumerable<TRequest>` plus an `IServerStreamWriter<TResponse>`.

```csharp
public override async Task StreamTelemetry(
    IAsyncStreamReader<TelemetryRequest> requestStream,
    IServerStreamWriter<TelemetryReply> responseStream,
    ServerCallContext context)
{
    var ctx = _contextFactory.Create("grpc-stream", context.CancellationToken);

    await Result.MapStreamAsync(requestStream.ReadAllAsync(context.CancellationToken), (req, token) => Result.Ok(req), context.CancellationToken)
        .FilterStreamAsync(req => !_muteList.Contains(req.TenantId), context.CancellationToken)
        .ForEachAsync(async (result, token) =>
        {
            if (result.IsFailure)
            {
                return result.CastFailure<Unit>();
            }

            var processed = await _processor.ProcessAsync(result.Value, token);
            if (processed.IsFailure)
            {
                return processed;
            }

            await responseStream.WriteAsync(new TelemetryReply { TenantId = result.Value.TenantId, Status = ReplyStatus.Ok }, token);
            return Result.Ok(Unit.Value);
        }, context.CancellationToken);
}
```

- Attach compensations for state mutated per request (e.g., `_processor` updates) using `ctx.RegisterCompensation`.
- Use `ResultPipelineChannels.MergeWithStrategyAsync` to send server push notifications alongside replies.
- Combine with `ResultPipeline.RetryAsync` inside the handler to gracefully absorb transient downstream failures while keeping the gRPC call alive.

## When to Use

Pick this pattern for sustained, high-volume streams where you need tight control over batching, retries, tenant isolation, and diagnostics. Hugo’s streaming combinators keep every stage observable and rollback-friendly even when millions of events are in flight.
