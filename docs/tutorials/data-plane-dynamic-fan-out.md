# Dynamic Fan-Out per Tenant With Result Pipelines

This tutorial shows how to route a shared ingress stream into tenant-specific processors using the new Result pipeline adapters. Each tenant gets its own channel reader, worker loop, and compensation scope, so failures in one tenant do not affect others while still rolling back side effects deterministically.

## Scenario

You ingest events from a shared gateway and need to fan them out per tenant:

1. Each tenant should process events independently.
2. Long-running tenant processors must inherit the parent cancellation token.
3. Cleanup (releasing buffers, undoing partial writes) must run if a tenant processor crashes or if the pipeline shuts down.
4. The solution should scale dynamically as tenants are added/removed.

## Architecture

- `ChannelReader<Envelope>` represents the shared ingress stream.
- `ResultPipelineChannels.FanOut` splits the stream into N tenant-specific readers.
- Each tenant runs a `WaitGroup` or `ErrGroup`-backed worker created via `ResultPipelineWaitGroupExtensions.Go`.
- Tenant processors use `Result<T>.WithCompensation` to register undo operations (e.g., delete staged blobs, revert ledger entries).

## Step 1 – Prepare the Context and Ingress Stream

```csharp
var scope = new CompensationScope();
var context = new ResultPipelineStepContext(
    "ingress.tenants",
    scope,
    TimeProvider.System,
    cancellationToken);

ChannelReader<Envelope> ingress = _ingressChannel.Reader;
```

## Step 2 – Create Tenant Branches With `FanOut`

Assume you have a lookup of active tenants and want one branch per tenant. The `channelFactory` parameter lets you customize buffering per branch.

```csharp
var tenantIds = _tenantRegistry.GetActiveTenantIds();
int branchCount = tenantIds.Count;

IReadOnlyList<ChannelReader<Envelope>> tenantReaders =
    ResultPipelineChannels.FanOut(
        context,
        ingress,
        branchCount: branchCount,
        completeBranches: true,
        channelFactory: _ => Channel.CreateBounded<Envelope>(new BoundedChannelOptions(1_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        }),
        cancellationToken: cancellationToken);
```

Each branch inherits the parent pipeline cancellation token; when the pipeline stops, `FanOut` registers compensations that drain the readers gracefully.

## Step 3 – Launch Tenant Processors via `WaitGroup.Go`

Use the `ResultPipelineWaitGroupExtensions.Go` helper so each processor runs with its own `ResultPipelineStepContext`.

```csharp
var waitGroup = new WaitGroup();

for (int i = 0; i < tenantReaders.Count; i++)
{
    string tenantId = tenantIds[i];
    ChannelReader<Envelope> tenantReader = tenantReaders[i];

    waitGroup.Go(context, async (childContext, token) =>
    {
        await foreach (var envelope in tenantReader.ReadAllAsync(token))
        {
            var result = await ProcessTenantMessageAsync(childContext, tenantId, envelope, token);
            if (result.IsFailure)
            {
                return result; // triggers compensation + upstream failure
            }
        }

        return Result.Ok(Unit.Value);
    }, stepName: $"tenant[{tenantId}]");
}
```

`childContext` automatically links to the parent scope. Per-tenant processors can register compensations (e.g., revert partial writes) that roll back if `ProcessTenantMessageAsync` fails.

## Step 4 – Implement Tenant Message Processing With Compensations

```csharp
private async ValueTask<Result<Unit>> ProcessTenantMessageAsync(
    ResultPipelineStepContext context,
    string tenantId,
    Envelope envelope,
    CancellationToken cancellationToken)
{
    return await Result.TryAsync(async ct =>
    {
        await _tenantValidators[tenantId].ValidateAsync(envelope.Payload, ct);

        context.RegisterCompensation(async token =>
        {
            await _tenantLedger.RollbackAsync(tenantId, envelope.MessageId, token);
        });

        await _tenantLedger.AppendAsync(tenantId, envelope.Payload, ct);
        await _outbox.PublishAsync(tenantId, envelope.Payload, ct);

        return Unit.Value;
    }, cancellationToken);
}
```

## Step 5 – Wait for Completion and Handle Failures

```csharp
var waitResult = await waitGroup.WaitAsync(cancellationToken);
if (waitResult.IsFailure)
{
    var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
    await Result.RunCompensationAsync(policy, scope, cancellationToken);
    _logger.LogError("Tenant processors aborted: {Error}", waitResult.Error);
}
```

If any tenant processor returns a failure, the parent context absorbs the error and the compensation scope replays tenant-specific cleanup in reverse order.

## Summary

- `ResultPipelineChannels.FanOut` dynamically splits the ingress stream into tenant-specific branches, linking cancellation tokens automatically.
- `ResultPipelineWaitGroupExtensions.Go` spins up long-lived processors that inherit the parent context and consolidate compensations back into the root scope.
- Per-tenant compensations (ledger rollback, outbox retractions) run deterministically whenever a tenant processor fails or the pipeline shuts down.
- The architecture scales horizontally by adding/removing tenants without rewriting orchestration logic.
