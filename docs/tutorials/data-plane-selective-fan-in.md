# Selective Fan-In With Priorities

Data-plane workloads often need to pull from multiple channels but prioritize certain sources (e.g., hotfix lanes, manual overrides, premium tenants). This tutorial shows how to use `ResultPipelineChannels.Select<TResult>` and `ChannelCase<T>` priorities to build a deterministic, compensation-aware fan-in that always serves high-priority lanes first.

## Scenario

You have three channels:

1. `manualOverrides`: must be processed immediately whenever a user submits an override.
2. `premiumQueue`: higher priority than the standard queue.
3. `standardQueue`: bulk traffic.

All three channels produce the same payload type (`Command`). The pipeline should:

- Always drain `manualOverrides` first when data is available.
- Drain `premiumQueue` next, then `standardQueue` if no higher priority cases are ready.
- Register compensations for each command (e.g., revert state transitions) so that if the pipeline aborts mid-command, you can roll back deterministically.

## Step 1 – Create the Pipeline Context

```csharp
var scope = new CompensationScope();
var context = new ResultPipelineStepContext(
    "command-router",
    scope,
    TimeProvider.System,
    cancellationToken);
```

## Step 2 – Prepare Channel Readers

```csharp
ChannelReader<Command> manualOverrides = _manualChannel.Reader;
ChannelReader<Command> premiumQueue = _premiumChannel.Reader;
ChannelReader<Command> standardQueue = _standardChannel.Reader;
```

## Step 3 – Compose the Select Workflow

Use the pipeline-aware builder to register cases with explicit priority values (lower numbers win).

```csharp
var builder = ResultPipelineChannels.Select<Unit>(context, timeout: TimeSpan.FromSeconds(1));

builder.Case(manualOverrides, priority: 0, (command, token) => ExecuteCommandAsync(context, command, token));
builder.Case(premiumQueue, priority: 1, (command, token) => ExecuteCommandAsync(context, command, token));
builder.Case(standardQueue, priority: 2, (command, token) => ExecuteCommandAsync(context, command, token));
builder.Default(async ct =>
{
    _metrics.RecordSelectTimeout();
    return Result.Ok(Unit.Value);
});

var selectResult = await builder.ExecuteAsync();
```

Each `ExecuteCommandAsync` invocation returns `Result<Unit>` and registers compensations for library, state, or downstream updates.

## Step 4 – Implement Command Execution With Compensations

```csharp
private async Task<Result<Unit>> ExecuteCommandAsync(ResultPipelineStepContext context, Command command, CancellationToken cancellationToken)
{
    return await Result.TryAsync(async ct =>
    {
        await _validator.EnsureAllowedAsync(command, ct);

        context.RegisterCompensation(async token =>
        {
            await _commandStore.RollbackAsync(command.Id, token);
        });

        await _commandStore.ApplyAsync(command, ct);
        return Unit.Value;
    }, cancellationToken);
}
```

If `ExecuteCommandAsync` fails, the select result becomes a failure and the compensation scope contains rollback entries for any previously applied state changes. When failure bubbles up, call `Result.RunCompensationAsync`.

## Step 5 – Loop Until Shutting Down

Wrap the select in a loop so the router keeps pulling from prioritized channels.

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    var result = await builder.ExecuteAsync();
    if (result.IsFailure)
    {
        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        await Result.RunCompensationAsync(policy, scope, cancellationToken);
        scope.Clear();
    }
}
```

After each iteration, the builder can be reused (cases remain registered). If compensations ran, clear the scope before the next iteration.

## Summary

- `ResultPipelineChannels.Select<TResult>` provides a fluent API for building Go-style selects that are aware of the current pipeline context.
- Priority values control which lanes win when multiple channels are ready.
- Default cases let you record metrics or trigger other logic when the select times out.
- Compensations registered during command execution are automatically absorbed into the parent context, so any failure or cancellation triggers deterministic rollback.
