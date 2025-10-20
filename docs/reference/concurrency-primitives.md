# Concurrency Primitives Reference

This reference describes the concurrency types exposed through `Hugo.Go`. Each section lists primary APIs, behaviour, and diagnostics emitted via `GoDiagnostics`.

## WaitGroup

Tracks asynchronous operations and delays shutdown until every task completes.

### Key members

- `WaitGroup.Add(int count)` / `Add(Task task)`
- `WaitGroup.Done()`
- `WaitGroup.WaitAsync(TimeSpan? timeout, TimeProvider? provider, CancellationToken cancellationToken)`
- Extension `WaitGroup.Go(Func<ValueTask>)`

### WaitGroup usage

```csharp
var wg = new WaitGroup();

wg.Go(async () =>
{
    await Task.Delay(50, cancellationToken);
});

var completed = await wg.WaitAsync(
    timeout: TimeSpan.FromMilliseconds(250),
    provider: timeProvider,
    cancellationToken: cancellationToken);
```

### WaitGroup notes

- `WaitAsync` returns `false` when a timeout elapses.
- Cancellation surfaces as `Error.Canceled` in result pipelines and `OperationCanceledException` otherwise.
- Diagnostics: `waitgroup.additions`, `waitgroup.completions`, `waitgroup.outstanding`.

## Mutex

Provides mutual exclusion with synchronous (`EnterScope`) and asynchronous (`LockAsync`) releasers.

### Mutex usage

```csharp
var mutex = new Mutex();

await using (await mutex.LockAsync(cancellationToken))
{
    // Exclusive access
}
```

### Mutex notes

- Pending waiters honour cancellation tokens.
- Dispose the releaser (via `await using`/`using`) to release the lock.

## RwMutex

Reader/writer lock that allows concurrent readers or a single writer.

### RwMutex usage

```csharp
var rwMutex = new RwMutex();

await using (await rwMutex.RLockAsync(ct))
{
    // Multiple readers permitted
}

await using (await rwMutex.LockAsync(ct))
{
    // Exclusive writer
}
```

### RwMutex notes

- Writer acquisition blocks new readers until released.
- Cancellation of a pending reader/writer propagates `OperationCanceledException`.

## Channels

`Go.MakeChannel<T>` creates unbounded or bounded channels for message passing. Fluent builders provide DI-friendly factories when you need to customise options once and share the channel through dependency injection.

### Overloads

- `MakeChannel<T>(int? capacity = null)`
- `MakeChannel<T>(BoundedChannelOptions options)`
- `MakePrioritizedChannel<T>(int priorityLevels, int defaultPriority)`
- `Go.BoundedChannel<T>(int capacity)`
- `Go.PrioritizedChannel<T>()` / `Go.PrioritizedChannel<T>(int priorityLevels)`

### Channel usage

```csharp
var channel = Go.MakeChannel<string>(capacity: 32);
await channel.Writer.WriteAsync("message", ct);
var value = await channel.Reader.ReadAsync(ct);
```

### Channel notes

- Bounded channels respect `FullMode` from `BoundedChannelOptions`.
- `TryComplete(Exception?)` propagates faults to readers.

### Channel builders

```csharp
services.AddBoundedChannel<Job>(capacity: 64, builder => builder
    .SingleWriter()
    .WithFullMode(BoundedChannelFullMode.DropOldest));

services.AddPrioritizedChannel<Job>(priorityLevels: 3, builder => builder
    .WithCapacityPerLevel(32)
    .WithDefaultPriority(1));
```

- `AddBoundedChannel<T>` registers a `Channel<T>` alongside its reader and writer.
- `AddPrioritizedChannel<T>` registers `PrioritizedChannel<T>`, the prioritized reader/writer helpers, and the base `ChannelReader<T>`/`ChannelWriter<T>` facades.
- Builders expose `.Build()` when you need an inline channel instance without DI.

## Select helpers

Await whichever channel case becomes ready first.

### Select APIs

- `Go.SelectAsync(IEnumerable<ChannelCase>, CancellationToken)`
- `Go.Select<TResult>()` fluent builder
- `ChannelCase.Create<T>(ChannelReader<T>, Func<T, CancellationToken, Task<Result<Unit>>>)`

### Select example

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

### Select notes

- Diagnostics capture attempts, completions, latency, cancellations.
- Timeouts use `TimeProvider` when supplied.
- Cancelled tokens surface as `Error.Canceled` with originating token metadata.
- `SelectBuilder` supports `.Default(...)` fallbacks, per-case `priority` ordering, and `.Deadline(...)` helpers for timer-driven outcomes.

## Fan-in utilities

- `Go.SelectFanInAsync(...)` repeatedly selects until all cases complete.
- `Go.FanInAsync(IEnumerable<ChannelReader<T>>, ChannelWriter<T>, bool completeDestination, CancellationToken)`
- `Go.FanIn(IEnumerable<ChannelReader<T>>, CancellationToken)` returns a new reader and internally completes the writer.

See [Coordinate fan-in workflows](../how-to/fan-in-channels.md) for step-by-step usage.

## Timers

Timer primitives mirror Go semantics while honouring `TimeProvider`.

### Timer APIs

- `Go.After(TimeSpan timeout, TimeProvider provider)`
- `Go.AfterAsync(TimeSpan timeout, TimeProvider provider, CancellationToken)`
- `Go.NewTicker(TimeSpan period, TimeProvider provider, CancellationToken)`
- `Go.Tick(TimeSpan period, TimeProvider provider, CancellationToken)`

### Timer example

```csharp
var provider = new FakeTimeProvider();

var once = await Go.AfterAsync(TimeSpan.FromSeconds(5), provider, ct);

await using var ticker = Go.NewTicker(TimeSpan.FromSeconds(1), provider, ct);
var tick = await ticker.ReadAsync(ct);
```

### Timer notes

- `GoTicker.Stop()` / `StopAsync()` dispose timers and complete the channel.
- Use fake time providers in tests for deterministic scheduling.
