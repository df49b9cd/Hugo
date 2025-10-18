# Concurrency Primitives

Hugo mirrors Go's approach to structured concurrency while embracing .NET idioms. The goal is predictable fan-out/fan-in workflows that honour cancellation and surface meaningful diagnostics.

## WaitGroup

`WaitGroup` tracks outstanding asynchronous operations. Add work with `Add`/`Add(Task)` or use the `Go` helpers.

```csharp
var wg = new WaitGroup();

wg.Go(async () =>
{
    await Task.Delay(50, cancellationToken);
});

// Times out after 250 ms if work is still pending.
var completed = await wg.WaitAsync(
    timeout: TimeSpan.FromMilliseconds(250),
    provider: timeProvider,
    cancellationToken: cancellationToken);
```

- `WaitAsync` supports both cancellation tokens and timeout semantics via `TimeProvider`.
- Diagnostics increment `waitgroup.additions`, `waitgroup.completions`, and `waitgroup.outstanding` counters.

## Mutex

`Mutex` provides synchronous (`EnterScope`) and asynchronous (`LockAsync`) mutual exclusion with disposable releasers.

```csharp
var mutex = new Mutex();

await using (await mutex.LockAsync(cancellationToken))
{
    // At most one writer executes this block.
}
```

- Cancellation-aware: pending waiters observe `OperationCanceledException`.
- Great for protecting shared state across async continuations without `lock`.

## RwMutex

`RwMutex` offers reader/writer semantics similar to Go's `sync.RWMutex`.

```csharp
await using (await rwMutex.RLockAsync(ct))
{
    // Many readers may enter concurrently.
}

await using (await rwMutex.LockAsync(ct))
{
    // Exclusive writer access.
}
```

- Reader acquisition is cancellable and blocks when a writer is active.
- Disposal releases the appropriate semaphore regardless of completion path.

## Channels

Channels coordinate producers and consumers with optional bounding.

```csharp
var channel = Go.MakeChannel<string>(capacity: 32);
await channel.Writer.WriteAsync("message", ct);
var value = await channel.Reader.ReadAsync(ct);
```

- `MakeChannel<T>` chooses between bounded/unbounded options, or build a `PrioritizedChannel<T>` for multi-queue scheduling.
- Writers and readers honour cancellation tokens on every async method.

### Prioritized Channels

`Go.MakePrioritizedChannel` orchestrates work across priority levels.

```csharp
var notifications = Go.MakePrioritizedChannel<string>(priorityLevels: 3, defaultPriority: 1);
await notifications.PrioritizedWriter.WriteAsync("system", priority: 0, ct);
```

## Select

`Go.SelectAsync` models Go's `select` keyword by awaiting the first ready channel case.

```csharp
var result = await Go.SelectAsync(
    timeout: TimeSpan.FromSeconds(1),
    cases: new[]
    {
        ChannelCase.Create(textChannel.Reader, async (text, ct) =>
        {
            Console.WriteLine($"text: {text}");
            return Result.Ok(Go.Unit.Value);
        }),
        ChannelCase.Create(signal.Reader, (unit, _) =>
        {
            Console.WriteLine("signal received");
            return Task.FromResult(Result.Ok(Go.Unit.Value));
        })
    },
    cancellationToken: ct);
```

- Built on `ChannelCase` continuations that produce `Result<Unit>`.
- Records attempts, completions, timeouts, and cancellations through `GoDiagnostics`.
- Supports deadlines via `TimeProvider` for deterministic testing.
