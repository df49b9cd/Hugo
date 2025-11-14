# Getting Started with Channels and Results

Build a minimal console application that produces, merges, and processes work using Hugo channels, wait groups, and `Result<T>`. By the end of the tutorial you will have an end-to-end pipeline that fans in multiple producers, validates messages, and shuts down gracefully on cancellation.

## What you will build

- Two producers feeding bounded channels.
- A fan-in relay that merges producers into a single channel.
- A consumer loop that transforms messages with result pipelines.
- Deterministic shutdown using `WaitGroup`, `CancellationToken`, and `Defer`.

Estimated time: 10–15 minutes.

## Prerequisites

- .NET 9 or .NET 10 SDK installed (`dotnet --version` should report `9.*` or `10.*`).
- A terminal and editor.
- A new folder for the sample (`mkdir HugoQuickstart` if you prefer to create it manually).

> **Tip:** Run `dotnet nuget locals all --clear` if a previous restore fails; the tutorial relies on the official `Hugo` NuGet package.

## Step&nbsp;1 — Create the project

```bash
dotnet new console -n HugoQuickstart
cd HugoQuickstart
dotnet add package Hugo
```

Restore completion confirms the package is available; fix restore issues before continuing.

## Step&nbsp;2 — Set up the program skeleton

Replace `Program.cs` with the following template:

```csharp
using Hugo;
using static Hugo.Go;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

var metrics = MakeChannel<int>(capacity: 8);
var jobs = MakeChannel<string>(capacity: 8);
var merged = MakeChannel<object>(capacity: 16);
var workers = new WaitGroup();
```

Highlights:

- `CancellationTokenSource` enforces a five second deadline for the demo.
- Two bounded channels (`metrics`, `jobs`) feed a shared destination (`merged`).
- `WaitGroup` tracks background work so you can block on completion deterministically.

## Step&nbsp;3 — Add producers

Append two `WaitGroup.Go` calls that populate the source channels and guarantee completion by deferring `TryComplete`:

```csharp
workers.Go(async () =>
{
    using var complete = Defer(() => metrics.Writer.TryComplete());

    for (var i = 0; i < 3; i++)
    {
        await metrics.Writer.WriteAsync(i, cts.Token).ConfigureAwait(false);
    }
});

workers.Go(async () =>
{
    using var complete = Defer(() => jobs.Writer.TryComplete());

    foreach (var name in new[] { "build", "deploy", "notify" })
    {
        await jobs.Writer.WriteAsync(name, cts.Token).ConfigureAwait(false);
    }
});
```

`Defer` mirrors Go's `defer` keyword and ensures the channel completes even if an exception occurs mid-loop.

## Step&nbsp;4 — Relay producers into a single channel

Merge the source readers into `merged.Writer`:

```csharp
var relay = FanInAsync(
    sources: new[] { metrics.Reader, jobs.Reader },
    destination: merged.Writer,
    completeDestination: true,
    cancellationToken: cts.Token);
```

- Passing `completeDestination: true` guarantees the merged channel completes once every source finishes.
- `FanInAsync` returns a task that resolves to `Result<Unit>`—you can inspect it later for failures.

## Step&nbsp;5 — Consume and transform messages

Add a result-aware consumer loop after the relay:

```csharp
var messages = new List<string>();

await foreach (var payload in merged.Reader.ReadAllAsync(cts.Token))
{
    var result = payload switch
    {
        int sample => Ok(sample)
            .Ensure(static value => value >= 0, value =>
                Error.From($"negative sample {value}", "error.validation"))
            .Map(static value => $"metric={value}"),
        string job => Ok(job)
            .Ensure(static value => !string.IsNullOrWhiteSpace(value))
            .Map(static value => $"job={value}"),
        _ => Err<string>("unsupported payload", "error.validation")
    };

    var handled = await result
        .TapAsync(static (value, token) =>
        {
            Console.WriteLine($"processed {value}");
            return ValueTask.CompletedTask;
        }, cts.Token);

    if (handled.IsSuccess)
    {
        messages.Add(handled.Value);
    }
    else
    {
        Console.WriteLine($"skipped: {handled.Error}");
    }
}
```

- `Ensure` validates data without throwing.
- `Map` transforms successful values, keeping the pipeline linear.
- `TapAsync` instruments successes without altering the result; switch to `TapValueTaskAsync` when the instrumentation already returns `ValueTask`.

## Step&nbsp;6 — Await completion and report failures

Finish the program with coordinated shutdown:

```csharp
var fanInResult = await relay;
if (fanInResult.IsFailure)
{
    Console.WriteLine($"fan-in failed: {fanInResult.Error}");
}

await workers.WaitAsync(cts.Token);

Console.WriteLine(string.Join(", ", messages));
```

`WaitAsync` observes the same cancellation token as the consumer loop, so the app exits cleanly when the deadline expires.

## Step&nbsp;7 — Run the sample

```bash
dotnet run
```

Expected output:

```
processed metric=0
processed metric=1
processed metric=2
processed job=build
processed job=deploy
processed job=notify
metric=0, metric=1, metric=2, job=build, job=deploy, job=notify
```

Try shortening the timeout or hitting `Ctrl+C` to see how `Error.Canceled` flows through the result pipeline without throwing.

## Optional — Add diagnostics in 60 seconds

```csharp
using Hugo.Diagnostics.OpenTelemetry;
using Microsoft.Extensions.Hosting;

builder.Services
    .AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = "hugo-quickstart";
        options.AddPrometheusExporter = true;
    });
```

Run the sample with a Prometheus collector (see [Publish metrics and traces to OpenTelemetry](../how-to/observe-with-opentelemetry.md)) to inspect counters such as `waitgroup.additions` and `channel.select.latency`.

## Verify your understanding

- Every async call accepted a `CancellationToken`.
- Producers always completed their writers, even when cancellation fired.
- The consumer treated validation failures as data, not exceptions.
- Shutdown awaited both the fan-in relay and the worker group.

If any of these checks surprised you, revisit the corresponding step before moving on.

## Next steps

- [Coordinate fan-in workflows](../how-to/fan-in-channels.md) for more advanced fan-in scenarios.
- [Publish metrics and traces to OpenTelemetry](../how-to/observe-with-opentelemetry.md) to light up observability.
- Explore the [`Hugo.WorkerSample`](../../samples/Hugo.WorkerSample/Program.cs) project for a full worker that combines queues, retries, and diagnostics.
