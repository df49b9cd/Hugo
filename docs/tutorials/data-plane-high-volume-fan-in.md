# High-Volume Fan-In With Deterministic Compensation

This tutorial walks through building a data-plane stage that drains multiple transport channels concurrently, validates each payload, and deterministically rolls back side effects when failures occur. You will wire the flow with `ResultPipelineStepContext`, `ResultPipelineChannels.FanInAsync`, and per-message compensations so that every record can clean up its resources even when the pipeline aborts mid-stream.

## Scenario

Imagine a worker that ingests events from several sources (Kafka partitions, SQS queues, lease-backed feeds). Each source exposes a `ChannelReader<T>`. For every item you need to:

1. Deserialize and validate the payload.
2. Persist an idempotency token (in case you need to replay later).
3. Emit domain events.
4. Remove the lease / ack the message only if processing succeeds.

You want best-effort throughput (multiple channels drained concurrently) while keeping deterministic compensation: if the pipeline fails after emitting domain events but before removing the lease, you need to roll back the events and release the lease.

## Prerequisites

- The adapters introduced in `ResultPipelineChannels` and `ResultPipelineTimers`.
- Basic familiarity with `Result<T>` and the `Functional.*` combinators.
- Channel readers for each transport. In this example we will mock them with `Channel.CreateUnbounded<T>()`.

## Step 1 – Build the Pipeline Context

Every data-plane stage begins with a `ResultPipelineStepContext`. It carries the shared time provider, cancellation token, and compensation scope.

```csharp
var scope = new CompensationScope();
var context = new ResultPipelineStepContext(
    stepName: "ingest.fan-in",
    compensationScope: scope,
    timeProvider: TimeProvider.System,
    cancellationToken: TestContext.Current.CancellationToken);
```

Any compensation registered through `context.RegisterCompensation(...)` will run if the pipeline fails or when you explicitly call `Result.RunCompensationAsync(...)`.

## Step 2 – Collect Channel Readers

Assume that each transport exposes a method returning `(ChannelReader<RawMessage> Reader, Func<Task> ReleaseAsync)`; you can project them into a list:

```csharp
IEnumerable<(ChannelReader<RawMessage> Reader, string Source)> sources = _feedRegistry
    .Select(feed => (feed.Reader, feed.Name));
```

## Step 3 – Define the Handler With Per-Message Compensations

The handler signature required by `ResultPipelineChannels.FanInAsync` is `Func<T, CancellationToken, ValueTask<Result<Unit>>>`. Inside the handler:

1. Deserialize the payload.
2. Persist the idempotency token.
3. Register compensations for the persisted token and any domain events you emit.
4. Return `Result.Ok(Unit.Value)` when everything succeeds or `Result.Fail<Unit>(Error.From(...))` when validation fails.

```csharp
ValueTask<Result<Unit>> HandleRawMessageAsync(
    RawMessage message,
    CancellationToken cancellationToken)
{
    return Result.TryAsync(async ct =>
    {
        var envelope = await _decoder.DecodeAsync(message, ct);
        await _idempotencyStore.RecordAsync(envelope.IdempotencyKey, ct);

        // Register compensations for deterministic rollback
        context.RegisterCompensation(envelope.IdempotencyKey, async (key, token) =>
        {
            await _idempotencyStore.DeleteAsync(key, token);
        });

        context.RegisterCompensation(async token =>
        {
            await _eventBus.PublishAsync(DomainEvents.Compensation(envelope), token);
        });

        await _eventBus.PublishAsync(envelope.DomainEvents, ct);
        await _leaseManager.CompleteAsync(message.LeaseId, ct);

        return Unit.Value;
    }, cancellationToken);
}
```

Because the handler returns a `Result<Unit>`, any exception automatically translates into `Error.FromException` and increments diagnostics.

## Step 4 – Drain the Readers With `ResultPipelineChannels.FanInAsync`

Invoke the adapter with the context, readers, handler, and optional timeout.

```csharp
ValueTask<Result<Unit>> DrainAsync(IEnumerable<ChannelReader<RawMessage>> readers, CancellationToken ct)
{
    return ResultPipelineChannels.FanInAsync(
        context,
        readers,
        HandleRawMessageAsync,
        timeout: TimeSpan.FromMinutes(1),
        cancellationToken: ct);
}
```

`FanInAsync` internally:

- Builds a Go-style `select` over the provided readers.
- Uses the pipeline’s time provider to evaluate the timeout.
- Ensures the handler receives a cancellation token linked to both the pipeline token and the call-site token.
- Absorbs all compensations recorded by the handler into the parent context.

## Step 5 – Run the Pipeline and Replay Compensations on Failure

Wrap the drain call with your preferred `Result<T>` combinators and run compensations if the pipeline fails.

```csharp
var result = await DrainAsync(sources.Select(s => s.Reader), context.CancellationToken);

if (result.IsFailure)
{
    var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
    await Result.RunCompensationAsync(policy, scope, CancellationToken.None);
    _metrics.RecordFanInFailure(result.Error!);
}
```

If the pipeline succeeds, `scope.HasActions` will be false because `ResultPipelineChannels.FanInAsync` clears per-message scopes for you.

## Summary

- `ResultPipelineChannels.FanInAsync` lets you drain multiple channel readers concurrently while keeping per-message compensations tied to the shared pipeline context.
- Every handler registers the clean-up logic it needs (`_idempotencyStore.DeleteAsync`, compensating domain events, lease release).
- When the pipeline fails, run `Result.RunCompensationAsync` once to replay all staged compensations in reverse registration order.
- Diagnostics remain consistent because each `Result<T>` creation increments `GoDiagnostics` counters only once.

You can combine this pattern with `ResultPipelineTimers` (to enforce SLAs per message) or `ResultPipelineWaitGroupExtensions.Go` (to spin additional background tasks) as your data plane grows.
