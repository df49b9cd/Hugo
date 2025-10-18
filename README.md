# Hugo

Go-style concurrency primitives and functional result pipelines for .NET 9/10 applications.

## Overview

Hugo brings Go-inspired building blocks—channels, wait groups, mutexes, and defer—to the .NET runtime while embracing railway-oriented programming for error handling. The library ships with observability hooks, prioritised channels, and integration helpers designed to keep asynchronous code predictable, testable, and composable.

## Installation

```bash
dotnet add package Hugo
```

- Targets `net9.0` and `net10.0`.
- Works seamlessly with central package management (`Directory.Packages.props`).
- To experiment locally, run `dotnet pack src/Hugo/Hugo.csproj -o ./artifacts` and reference the generated `.nupkg` from a local feed.

## Quickstart

```csharp
using Hugo;
using static Hugo.Go;

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var channel = MakeChannel<string>(capacity: 4);
var workers = new WaitGroup();

workers.Go(async () =>
{
    using var scope = Defer(() => channel.Writer.TryComplete());

    await channel.Writer.WriteAsync("hello", cts.Token);
    await channel.Writer.WriteAsync("world", cts.Token);
});

var messages = new List<string>();
while (await channel.Reader.WaitToReadAsync(cts.Token))
{
    if (channel.Reader.TryRead(out var value))
    {
        var result = Ok(value)
            .Ensure(static text => !string.IsNullOrWhiteSpace(text))
            .Map(static text => text.ToUpperInvariant());

        if (result.IsSuccess)
        {
            messages.Add(result.Value);
        }
    }
}

await workers.WaitAsync(cts.Token);
Console.WriteLine(string.Join(' ', messages));
```

## Key Features

- **Go primitives for .NET**: Wait groups, mutexes, RW locks, prioritized channels, deferred cleanup, and select-style coordination.
- **Railway-oriented results**: `Result<T>` pipelines with `Then`, `Map`, `Recover`, `Ensure`, `Tap`, and friends for deterministic error handling.
- **Observability hooks**: Metered counters and histograms via `GoDiagnostics` for wait-group activity, result creation, and channel select latency.
- **Cancellation-first design**: All asynchronous APIs accept `CancellationToken`, wrap cancellations consistently, and surface `Error.Canceled` in pipelines.

## Deep Dives

- [Concurrency primitives](docs/concurrency.md)
- [Result pipelines](docs/results.md)
- [Diagnostics & metrics](docs/diagnostics.md)
- [Samples & benchmarks](docs/samples.md)

## Samples & Benchmarks

- `samples/Hugo.WorkerSample`: Background worker demonstrating wait groups, channels, and defer for structured concurrency.
- `benchmarks/Hugo.Benchmarks`: BenchmarkDotNet suite comparing Hugo mutexes against native `SemaphoreSlim` primitives.

## Contributing

1. Install the .NET 10 preview SDK alongside .NET 9.
2. Run `dotnet build Hugo.slnx` and `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj` before submitting changes.
3. Collect coverage locally with `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj --collect:"XPlat Code Coverage"` to mirror CI.
4. See [docs/roadmap.md](docs/roadmap.md) for upcoming primitives and contribution guidelines.
5. Ensure new APIs include XML docs and tests covering cancellation paths.
