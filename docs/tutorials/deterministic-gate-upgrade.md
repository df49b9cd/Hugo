# Deterministic Gate & Effect Store (Socket/GPRC Upgrades)

`DeterministicGate` and `DeterministicEffectStore` ensure idempotent side-effects when rolling out new transport layers. This tutorial demonstrates:

- Gradually switching from legacy sockets to gRPC streaming.
- Recording effects (sent batches, acknowledgements) so replays don’t double-send.
- Capturing upgrade decisions via `DeterministicGate`.
- Rolling back staged work if the new path fails.

## Scenario

A worker currently pushes telemetry over raw sockets. You plan to upgrade to gRPC streaming. During rollout:

1. Each tenant passes through `DeterministicGate.Require` to decide path (“legacy” vs “grpc”).
2. For gRPC, every batch is captured in `DeterministicEffectStore`; replays reuse stored success/failure.
3. On failure, compensations revert staged data and gate decisions.

## Gate Setup

```csharp
var gate = DeterministicGate.Create(
    _deterministicStore,
    new DeterministicGateOptions
    {
        ChangeId = $"grpc-upgrade::{tenantId}",
        MinVersion = new Version(1, 0),
        MaxVersion = new Version(1, 0)
    });
```

## Execution Flow

```csharp
var decision = await gate.ExecuteAsync(async ctx =>
{
    var branch = ctx.Decision.IsNew ? UpgradeBranch.Grpc : UpgradeBranch.Legacy;
    return branch switch
    {
        UpgradeBranch.Grpc => await SendViaGrpcAsync(ctx, tenantId, cancellationToken),
        _ => await SendViaSocketAsync(ctx, tenantId, cancellationToken)
    };
},
cancellationToken);
```

### Capturing Effects (gRPC path)

```csharp
async ValueTask<Result<Unit>> SendViaGrpcAsync(
    DeterministicGate.DeterministicWorkflowContext ctx,
    string tenantId,
    CancellationToken cancellationToken)
{
    return await ctx.Effects.CaptureAsync(
        effectId: $"grpc-send::{tenantId}",
        execute: async token =>
        {
            using var call = _grpcClient.StreamTelemetry(cancellationToken: token);
            await foreach (var batch in _batchSource.ForTenant(tenantId, token))
            {
                await call.RequestStream.WriteAsync(batch, token);
            }

            await call.RequestStream.CompleteAsync();
            return Result.Ok(Unit.Value);
        },
        rehydrate: result => result,
        cancellationToken: cancellationToken);
}
```

If the worker crashes after sending, the effect store already contains `Result.Ok(Unit.Value)`, so replays skip re-sending the batch.

### Legacy Path with Compensations

```csharp
async ValueTask<Result<Unit>> SendViaSocketAsync(
    DeterministicGate.DeterministicWorkflowContext ctx,
    string tenantId,
    CancellationToken cancellationToken)
{
    var pipelineCtx = new ResultPipelineStepContext(
        $"socket-{tenantId}",
        ctx.CompensationScope,
        ctx.TimeProvider,
        cancellationToken);

    pipelineCtx.RegisterCompensation(_ => _legacySocket.RollbackAsync(tenantId, cancellationToken));

    return await ResultPipeline.RetryAsync(
        (_, token) => _legacySocket.SendAsync(tenantId, token),
        policy: ResultExecutionPolicy.ExponentialRetry(3, TimeSpan.FromMilliseconds(300)),
        cancellationToken: cancellationToken);
}
```

### Recording Upgrade Completion

After the gRPC path succeeds, mark the gate decision so future runs skip legacy logic:

```csharp
if (decision.IsSuccess && decision.Value.Path == UpgradeBranch.Grpc)
{
    await gate.MarkCompletedAsync(cancellationToken);
}
```

## Replays & Idempotency

- `DeterministicGate` ensures only one branch executes per (`ChangeId`, tenant). Replays reuse stored results so upgrading doesn’t duplicate network sends.
- `DeterministicEffectStore` entries persist across crashes. On replay, `CaptureAsync` immediately returns the previous `Result<Unit>`, so downstream compensations are not re-registered.

## gRPC Server Considerations

On the server side, capture inbound batches with `DeterministicEffectStore` before applying them to state. When the server restarts, replays read stored results instead of reapplying the same batch.

```csharp
public override async Task StreamTelemetry(...)
{
    await _effectStore.CaptureAsync(
        effectId: $"ingest::{tenantId}::{batchId}",
        execute: async token => await _ingestor.ApplyAsync(batch, token),
        rehydrate: result => result,
        cancellationToken: context.CancellationToken);
}
```

## Summary

Combining `DeterministicGate` with `DeterministicEffectStore` lets you orchestrate transport upgrades without double-sending data. Hugo’s Result pipelines keep both the legacy and new paths consistent with retries, compensations, and diagnostics. 
