# Duplex Socket Pipelines

Duplex connections need simultaneous read/write pumps plus routing logic for request/response pairs. With Hugo you can keep both halves deterministic, cancelable, and testable.

## Scenario

- Maintain a persistent TCP connection to a trading gateway.
- Send commands while also consuming market data pushed by the gateway.
- Correlate responses to requests (first success wins); cancel losers.
- Apply error compensation (undo partially applied orders) if the socket drops.

## Architecture

```
Outbound Pump: IAsyncEnumerable<Result<Command>>
      │   │
      │   └─ Result.ForEachLinkedCancellationAsync → socket.WriteAsync
Inbound Pump: Socket reader → Result.FlatMapStreamAsync (frames → messages)
      │
      ├─ ResultPipelineChannels.SelectAsync (correlate replies vs. timeout)
      └─ ResultPipelineChannels.MergeWithStrategyAsync (market data + replies)
```

## Outbound Pump

```csharp
async ValueTask<Result<Unit>> RunSendLoopAsync(
    IAsyncEnumerable<Result<Command>> commands,
    ResultPipelineStepContext ctx,
    CancellationToken token)
{
    return await commands.ForEachLinkedCancellationAsync(async (result, linkedToken) =>
    {
        if (result.IsFailure)
        {
            return result.CastFailure<Unit>();
        }

        ctx.RegisterCompensation(_ => _commandStore.MarkFailedAsync(result.Value.Id, linkedToken));
        await _socket.WriteAsync(result.Value.Serialize(), linkedToken);
        return Result.Ok(Unit.Value);
    }, token);
}
```

## Inbound Pump

```csharp
IAsyncEnumerable<Result<Message>> messageStream = Result.FlatMapStreamAsync(
    _socket.ReadFramesAsync(ct),
    async (frame, token) =>
    {
        var parsed = await ParseAsync(frame, token);
        yield return parsed;
        if (parsed.IsOrderAck)
        {
            ctx.RegisterCompensation(_ => _orderBook.RollbackAsync(parsed.OrderId, token));
        }
    },
    ct);
```

## Correlating Replies

```csharp
var selectBuilder = ResultPipelineChannels.Select<ResponseContext>(ctx, timeout: TimeSpan.FromSeconds(2));
foreach (var pending in _pendingOrders)
{
    selectBuilder.Case(
        pending.Channel.Reader,
        state => Result.Ok(new ResponseContext(pending.CorrelationId, state)));
}

var selectResult = await selectBuilder.ExecuteAsync();
if (selectResult.IsFailure)
{
    await Result.RunCompensationAsync(_policy, ctx.Scope, ct);
}
```

## Merging Streams

Merge unsolicited market data with correlated responses via `ResultPipelineChannels.MergeWithStrategyAsync`, choosing a strategy that prioritizes control replies but still drains data feeds.

```csharp
await ResultPipelineChannels.MergeWithStrategyAsync(
    ctx,
    new[] { _replyChannel.Reader, _marketDataChannel.Reader },
    _dispatchChannel.Writer,
    (readers, token) => new ValueTask<int>(_priorityScheduler.Next(readers)),
    cancellationToken: ct);
```

## Heartbeats & Recovery

- Use `ResultPipelineTimers.NewTicker` to send ping frames and detect idle sockets.
- Wrap reconnect logic in `ResultPipeline.RetryAsync`; if the connection fails, compensation scopes unwind pending operations before attempting to reconnect.

## When to Use

Duplex patterns excel when you must keep a bidirectional socket hot while guaranteeing per-message compensation, cancellation, and correlation. Hugo’s pumps + select/fan-in primitives help you do this without scattering stateful logic across your service.

## gRPC Duplex Client Example

.NET gRPC exposes `AsyncDuplexStreamingCall<TRequest, TResponse>` with `RequestStream` / `ResponseStream`. Plug each side into the pipelines above:

```csharp
async ValueTask<Result<Unit>> RunGrpcDuplexAsync(
    AsyncDuplexStreamingCall<Command, Reply> call,
    IAsyncEnumerable<Result<Command>> outbound,
    ResultPipelineStepContext ctx,
    CancellationToken cancellationToken)
{
    var sendLoop = RunSendLoopAsync(
        outbound,
        ctx,
        async (command, token) =>
        {
            await call.RequestStream.WriteAsync(command, token);
            return Result.Ok(Unit.Value);
        },
        cancellationToken);

    var receiveLoop = Result.FlatMapStreamAsync(
        call.ResponseStream.ReadAllAsync(cancellationToken),
        async (reply, token) =>
        {
            yield return Result.Ok(reply);
            if (reply.IsAck)
            {
                ctx.RegisterCompensation(_ => _orderBook.RollbackAsync(reply.OrderId, token));
            }
        },
        cancellationToken)
        .ForEachAsync(ProcessReplyAsync, cancellationToken);

    var combined = await Result.WhenAll(new[] { sendLoop, receiveLoop }, cancellationToken: cancellationToken);
    return combined.Bind(_ => Result.Ok(Unit.Value));
}
```

- **Backpressure:** The send loop uses `ForEachLinkedCancellationAsync` to respect cancellation from either the pipeline or gRPC call.
- **Heartbeats:** Use `ResultPipelineTimers.NewTicker` to periodically `WriteAsync(new Command { Heartbeat = ... })`.
- **Failure handling:** When `ResponseStream` throws, the receive loop returns failure, triggering compensations and cancelling the send loop.
