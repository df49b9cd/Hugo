# MeshKit: Peer Discovery, Gossip, and Leader Election with Hugo

MeshKit is a background service that discovers peers, exchanges gossip, and elects regional/global leaders over secure gRPC (HTTP/3 + mTLS). This tutorial shows how to compose Hugo primitives—Result pipelines, SafeTaskQueue, deterministic gate/effect store, and channel adapters—to build such a service.

## Goals

- **Service discovery & gossip:** Continuously stream membership and state updates between peers.
- **Leader election:** Race heartbeats/timeouts to elect regional and global coordinators.
- **Durability & retries:** Buffer work via SafeTaskQueue when peers are unreachable.
- **Deterministic upgrades:** Roll out new election logic without double-sending.
- **Security:** All gRPC streams run over HTTP/3 with mTLS; certificate lifecycle is managed through Result compensations.
- **Observability:** Metrics, heartbeats, and poison queues flag unhealthy peers.

## High-Level Architecture

```
SafeTaskQueue<MeshMessage> ──> TaskQueueChannelAdapter ──> Result pipelines ──> gRPC streaming (HTTP/3 + mTLS)
                                           │                      │
                                           │                      ├─ select-based leader election (heartbeats + timers)
                                           │                      └─ gossip fan-out (MergeWithStrategyAsync)
                           DeterministicGate/EffectStore guard rollout of new logic (legacy sockets vs gRPC)
```

## Components & Hugo Building Blocks

| Concern | Hugo Feature | Purpose |
| --- | --- | --- |
| Work buffering | `SafeTaskQueue<T>` + `TaskQueueChannelAdapter<T>` | Durable leasing, poison handling, automatic pumps feeding Result streams |
| Streaming transport | `Result.MapStreamAsync`, `Result.FlatMapStreamAsync`, `ForEachAsync` | Convert gRPC request/response streams into Result-aware sequences |
| Leader election | `ResultPipelineChannels.SelectAsync`, `Go.RaceValueTaskAsync`, `ResultPipelineTimers.NewTicker` | Race heartbeats vs. timeouts, run multiple election cases |
| Gossip merge | `ResultPipelineChannels.MergeWithStrategyAsync`, `BroadcastAsync` | Combine unsolicited gossip, replies, and control messages |
| Upgrade orchestration | `DeterministicGate`, `DeterministicEffectStore` | Toggle between legacy socket path and gRPC path without duplicate effects |
| Security | Result compensations + `ResultPipeline.RetryAsync` | Stage certificates, tear down on failure, retry handshake/sends |
| Observability | `TapSuccessEachAsync`, `TapFailureEachAsync`, `CollectErrorsAsync`, `Tap*AggregateErrorsAsync`, `Tap*IgnoreErrorsAsync` | Emit metrics, choose per-stream failure semantics (aggregate vs ignore) |

## Step 1 – Secure gRPC Client/Server Setup

```csharp
var handler = new SocketsHttpHandler
{
    SslOptions =
    {
        ClientCertificates = { _certLoader.LoadClientCertificate() },
        RemoteCertificateValidationCallback = _certValidator.ValidatePeerCertificate
    },
    EnableMultipleHttp2Connections = true
};

var channel = GrpcChannel.ForAddress(
    meshPeer.Endpoint,
    new GrpcChannelOptions
    {
        HttpHandler = handler,
        Credentials = ChannelCredentials.Create(new SslCredentials(_caCerts), CallCredentials.FromInterceptor(AddMetadata)),
        UnsafeUseInsecureChannelCallCredentials = false
    });

var meshClient = new Mesh.MeshClient(channel);
```

Use Hugo compensations to manage certificates:

```csharp
ctx.RegisterCompensation(_ => _certLoader.DisposeCertificates());
```

## Step 2 – SafeTaskQueue Pumps for Outbound Messages

```csharp
var meshQueue = SafeTaskQueue<MeshMessage>.Create(new SafeTaskQueueOptions
{
    MaxRetries = 5,
    PoisonQueueName = "mesh-poison"
});

var adapter = TaskQueueChannelAdapter<MeshMessage>.Create(
    meshQueue,
    concurrency: Environment.ProcessorCount,
    ownsQueue: false);

var outboundCtx = _contextFactory.Create("mesh-outbound", cancellationToken);

await adapter.Reader
    .ReadAllAsync(cancellationToken)
    .Select(lease => Result.Ok(lease))
    .ForEachLinkedCancellationAsync(async (leaseResult, token) =>
    {
        if (leaseResult.IsFailure)
        {
            return leaseResult.CastFailure<Unit>();
        }

        var lease = leaseResult.Value;
        outboundCtx.RegisterCompensation(_ => meshQueue.FailAsync(lease, Error.From("rollback"), requeue: true, token));

        var sendResult = await SendMessageAsync(lease.Payload, outboundCtx, token);
        if (sendResult.IsFailure)
        {
            await meshQueue.FailAsync(lease, sendResult.Error!, requeue: !lease.IsPoisoned, token);
            return sendResult.CastFailure<Unit>();
        }

        await meshQueue.AckAsync(lease, token);
        return Result.Ok(Unit.Value);
    },
    cancellationToken);
```

`SendMessageAsync` will route messages to the gRPC duplex stream described below.

## Step 3 – Duplex gRPC Stream for Gossip & Control

```csharp
async Task<Result<Unit>> RunMeshStreamAsync(
    ResultPipelineStepContext ctx,
    CancellationToken cancellationToken)
{
    using var call = meshClient.MeshStream(
        headers: _metadataProvider(),
        deadline: DateTime.UtcNow.AddMinutes(5),
        cancellationToken: cancellationToken);

    var send = SendLoopAsync(call.RequestStream, ctx, cancellationToken);
    var receive = ReceiveLoopAsync(call.ResponseStream, ctx, cancellationToken);

    var joined = await Result.WhenAll(new[] { send, receive }, cancellationToken: cancellationToken);
    return joined.Bind(_ => Result.Ok(Unit.Value));
}
```

### Send Loop (writing to gRPC)

```csharp
async ValueTask<Result<Unit>> SendLoopAsync(
    IClientStreamWriter<MeshMessage> writer,
    ResultPipelineStepContext ctx,
    CancellationToken token)
{
    // Outbound messages already modeled as IAsyncEnumerable<Result<MeshMessage>>
    return await _outboundMessages.ForEachLinkedCancellationAsync(async (result, linkedToken) =>
    {
        if (result.IsFailure)
        {
            return result.CastFailure<Unit>();
        }

        await writer.WriteAsync(result.Value, linkedToken);
        return Result.Ok(Unit.Value);
    }, token);
}
```

### Receive Loop (reading from gRPC)

```csharp
async ValueTask<Result<Unit>> ReceiveLoopAsync(
    IAsyncStreamReader<MeshMessage> reader,
    ResultPipelineStepContext ctx,
    CancellationToken token)
{
    var stream = Result.MapStreamAsync(
        reader.ReadAllAsync(token),
        (message, _) => Result.Ok(message),
        token);

    var gossipHub = Channel.CreateUnbounded<Result<MeshMessage>>();
    _ = stream.ToChannelAsync(gossipHub.Writer, token);

    return await ResultPipelineChannels.MergeWithStrategyAsync(
        ctx,
        new[] { gossipHub.Reader, _localControlChannel.Reader },
        _dispatchChannel.Writer,
        (readers, _) => new ValueTask<int>(_scheduler.Next(readers)),
        cancellationToken: token);
}
```

## Step 4 – Leader Election with Select + Timers

```csharp
async ValueTask<Result<LeaderDecision>> ElectLeaderAsync(
    ResultPipelineStepContext ctx,
    CancellationToken cancellationToken)
{
    var heartbeatTicker = ResultPipelineTimers.NewTicker(ctx, TimeSpan.FromSeconds(3), cancellationToken);
    var electionTimeout = ResultPipelineTimers.NewTicker(ctx, TimeSpan.FromSeconds(10), cancellationToken);

    var builder = ResultPipelineChannels.Select<LeaderDecision>(ctx);
    builder.Case(_incomingVotes.Reader, (vote, token) => HandleVoteAsync(vote, token));
    builder.Case(heartbeatTicker.Reader, (_, token) => SendHeartbeatAsync(token));
    builder.Case(electionTimeout.Reader, (_, token) => TriggerElectionAsync(token));

    return await builder.ExecuteAsync();
}
```

- `SendHeartbeatAsync` publishes a message to all peers via the gRPC send loop.
- `TriggerElectionAsync` increments the local term, records a vote, and dispatches election requests (again through the outbound queue).
- `HandleVoteAsync` updates local state; if a quorum is reached, register a compensation to demote if a more recent term is seen.

Use `Go.RaceValueTaskAsync` to resolve race conditions (e.g., first quorum vs. timeout vs. term bump).

## Step 5 – Deterministic Gate for Upgrade Control

Wrap the entire MeshKit run loop inside a deterministic gate so you can switch between “legacy socket mesh” and “gRPC mesh” per region/tenant:

```csharp
var gate = DeterministicGate.Create(_deterministicStore, new DeterministicGateOptions
{
    ChangeId = $"meshkit::{region}",
    MinVersion = new Version(1, 0),
    MaxVersion = new Version(1, 1)
});

await gate.ExecuteAsync(async workflowCtx =>
{
    return workflowCtx.Decision.IsNew
        ? await RunGrpcMeshAsync(workflowCtx, cancellationToken)
        : await RunLegacyMeshAsync(workflowCtx, cancellationToken);
}, cancellationToken);
```

Inside `RunGrpcMeshAsync`, capture critical side effects (e.g., “sent gossip batch”, “updated leader state”) with `workflowCtx.Effects.CaptureAsync` so replays skip duplicates.

## Step 6 – SafeTaskQueue & Poison Handling

Continue to monitor the SafeTaskQueue:

```csharp
await meshQueue.PoisonReader.ReadAllAsync(ct)
    .TapSuccessEachAggregateErrorsAsync((poison, _) => _metrics.RecordPoison(poison.MessageType));
```

Provide remediation steps (e.g., dead-letter storage or manual intervention) via compensations or optional `Result.Pipeline.RetryAsync`.

## Step 7 – Observability

- `TapSuccessEachAsync`/`TapFailureEachAsync` on outbound/inbound streams record per-peer success/failure counts; swap to `AggregateErrorsAsync` for dashboards that need every error, or `IgnoreErrorsAsync` for fire-and-forget telemetry.
- `CollectErrorsAsync` aggregates failures over time; you can log them or push to a monitoring system.
- Use `ResultPipelineTimers` to emit heartbeat metrics even if no gossip is flowing.

## Summary

MeshKit leverages Hugo’s deterministic, streaming-friendly primitives:

- **SafeTaskQueue + TaskQueueChannelAdapter** ensure durable backpressure.
- **Result pipelines** keep gRPC HTTP/3 streams composable (fan-out, merging, filtering).
- **Select, timers, and race helpers** implement leader election without ad-hoc threading.
- **DeterministicGate + EffectStore** orchestrate upgrades and prevent duplicate side effects.
- **Compensations, retries, and diagnostics** keep the system self-healing under failures.

With these building blocks, MeshKit stays resilient, observable, and upgrade-friendly even as the cluster topology evolves.
