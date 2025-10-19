# Hugo

[![CI](https://github.com/df49b9cd/Hugo/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/df49b9cd/Hugo/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Hugo.svg?logo=nuget)](https://www.nuget.org/packages/Hugo)

Go-style concurrency primitives and functional result pipelines for .NET 9/10 applications.

## Overview

Hugo brings Go-inspired building blocks—channels, wait groups, mutexes, timers, and defer—to the .NET runtime while embracing railway-oriented programming for error handling. The library ships with observability hooks, prioritised channels, and integration helpers designed to keep asynchronous code predictable, testable, and composable.

- **Structured docs**: Tutorials, how-to guides, reference, and explanations live under [`docs/index.md`](docs/index.md).
- **Deterministic concurrency**: `WaitGroup`, `Mutex`, `RwMutex`, and `SelectAsync` mirror Go semantics while honouring cancellation tokens and `TimeProvider`.
- **Functional results**: `Result<T>` pipelines keep happy-path code linear and report failures with structured metadata.
- **Observability ready**: `GoDiagnostics` exposes counters and histograms that plug straight into OpenTelemetry exporters.
- **Benchmarks & samples**: `benchmarks/Hugo.Benchmarks` compares primitives under load; `samples/Hugo.WorkerSample` shows structured background work.

## Compatibility

- Targets `net9.0` and `net10.0`.
- Plays nicely with host builders, ASP.NET background services, and worker services.
- Tested with the .NET 10 preview SDK; install alongside .NET 9 for full coverage.

## Installation

```bash
dotnet add package Hugo
```

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

## Documentation

- [Documentation index](docs/index.md): entry point for tutorials, how-to guides, reference, and explanations.
- [Getting started tutorial](docs/tutorials/getting-started.md): build a minimal worker with channels and results.
- [Fan-in how-to](docs/how-to/fan-in-channels.md): merge multiple readers with cancellation and deadlines.
- [OpenTelemetry how-to](docs/how-to/observe-with-opentelemetry.md): export `GoDiagnostics` metrics.
- [Concurrency primitives reference](docs/reference/concurrency-primitives.md).
- [Result pipeline reference](docs/reference/result-pipelines.md).
- [Design principles](docs/explanation/design-principles.md).

## Samples & Benchmarks

- `samples/Hugo.WorkerSample`: Background worker demonstrating wait groups, channels, and defer for structured concurrency.
- `benchmarks/Hugo.Benchmarks`: BenchmarkDotNet suite comparing Hugo mutexes against native `SemaphoreSlim` primitives.

## Support & policies

- Questions & bugs: [GitHub issues](https://github.com/df49b9cd/Hugo/issues)
- Security disclosures: contact the maintainer privately before filing a public issue.
- Roadmap: keep up with priorities in [`docs/meta/roadmap.md`](docs/meta/roadmap.md).

## Contributing

1. Install the .NET 10 preview SDK alongside .NET 9.
2. Run `dotnet build Hugo.slnx` and `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj` before submitting changes.
3. Collect coverage locally with `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj --collect:"XPlat Code Coverage"` to mirror CI.
4. Review [`docs/index.md`](docs/index.md) to update tutorials, how-to guides, and references when behaviour changes.
5. Track strategic work in [`docs/meta/roadmap.md`](docs/meta/roadmap.md) and ensure new APIs include XML docs plus cancellation coverage tests.

## License

License declaration is pending; open an issue if you need clarification before adopting the library in production.
