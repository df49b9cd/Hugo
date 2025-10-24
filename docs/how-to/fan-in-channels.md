# Coordinate Fan-In Workflows

Use this guide when you need to merge multiple channel readers into a single pipeline and stop cleanly on cancellation.

## Scenario

You receive metrics from one channel and jobs from another. You want to forward them into a single queue while respecting cancellation tokens and ensuring the destination completes correctly.

## Steps

1. **Set up channels and cancellation**

    ```csharp
    using Hugo;
    using static Hugo.Go;

    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var metrics = MakeChannel<int>(capacity: 16);
    var jobs = MakeChannel<string>(capacity: 16);
    var merged = MakeChannel<object>();
    ```

2. **Start producers**

   ```csharp
   var wg = new WaitGroup();

   wg.Go(async () =>
   {
       for (var i = 0; i < 10; i++)
       {
           await metrics.Writer.WriteAsync(i, cts.Token);
       }
       metrics.Writer.TryComplete();
   });

   wg.Go(async () =>
   {
       string[] names = ["build", "deploy", "notify"];
       foreach (var name in names)
       {
           await jobs.Writer.WriteAsync(name, cts.Token);
       }
       jobs.Writer.TryComplete();
   });
   ```

3. **Fan in using `Go.FanInAsync`**

   ```csharp
   var relay = FanInAsync(
       sources: new[] { metrics.Reader, jobs.Reader },
       destination: merged.Writer,
       completeDestination: true,
       cancellationToken: cts.Token);
   ```

   - `completeDestination: true` tells Hugo to call `TryComplete` on the destination when all sources finish.
   - If any source faults, the error propagates to `merged.Writer.TryComplete(error)` so downstream readers see the failure.

4. **Process merged values**

   ```csharp
   await foreach (var payload in merged.Reader.ReadAllAsync(cts.Token))
   {
       switch (payload)
       {
           case int sample:
               Console.WriteLine($"metric {sample}");
               break;
           case string job:
               Console.WriteLine($"job {job}");
               break;
       }
   }
   ```

5. **Join background work**

    ```csharp
    await relay;
    await wg.WaitAsync(cts.Token);
    ```

## Troubleshooting

- **Early cancellation**: Pass a linked token to `FanInAsync`. If cancellation fires, the task completes with `Error.Canceled` and the destination writer is completed with that error so consumers can respond.
- **Backpressure**: Provide `BoundedChannelOptions` when calling `MakeChannel` to cap queue length and avoid unbounded growth.
- **Global deadlines**: When you provide a `timeout` (or `deadline`) to `SelectFanInAsync`/`FanInAsync`, the helper records a single absolute deadline using the supplied `TimeProvider` and applies it to the entire fan-in loop. Once the deadline passes, the operation ends with `Error.Timeout` even if individual select iterations still have ready readers.
- **Fan-in termination**: Set `completeDestination` to `false` when the destination is managed elsewhere; otherwise you risk completing a shared writer twice.

## Related topics

- [`Go.SelectFanInAsync`](../reference/concurrency-primitives.md#selectfaninasync) for looping fan-in logic without creating a new channel.
- [Publish metrics to OpenTelemetry](observe-with-opentelemetry.md) to observe backlog depth via `GoDiagnostics`.
