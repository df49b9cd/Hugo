# Hugo

[![CI](https://github.com/df49b9cd/Hugo/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/df49b9cd/Hugo/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Hugo.svg?logo=nuget)](https://www.nuget.org/packages/Hugo)
[![NuGet Diagnostics](https://img.shields.io/nuget/v/Hugo.Diagnostics.OpenTelemetry.svg?logo=nuget&label=Hugo.Diagnostics.OpenTelemetry)](https://www.nuget.org/packages/Hugo.Diagnostics.OpenTelemetry)

Go-style concurrency primitives and functional result pipelines for .NET 10 applications.

## Overview

Hugo brings Go-inspired building blocks—channels, wait groups, mutexes, timers, and defer—to the .NET runtime while embracing railway-oriented programming for error handling. The library ships with observability hooks, prioritised channels, and integration helpers designed to keep asynchronous code predictable, testable, and composable.

- **Structured docs**: Tutorials, how-to guides, reference, and explanations live under [`docs/index.md`](docs/index.md).
- **Deterministic concurrency**: `WaitGroup`, `Mutex`, `RwMutex`, and `SelectAsync` mirror Go semantics while honouring cancellation tokens and `TimeProvider`.
- **Functional results**: `Result<T>` pipelines keep happy-path code linear and report failures with structured metadata.
- **Observability ready**: `GoDiagnostics` exposes counters and histograms that plug straight into OpenTelemetry exporters.
- **Benchmarks & samples**: `benchmarks/Hugo.Benchmarks` compares primitives under load; `samples/Hugo.WorkerSample` shows structured background work.

## Compatibility

- Targets `net10.0`.
- Plays nicely with host builders, ASP.NET background services, and worker services.
- Tested with the .NET 10 SDK.
- Native AOT ready: libraries run under trimming, and the worker sample publishes with `dotnet publish -p:PublishAot=true`.

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

- `samples/Hugo.WorkerSample`: Background worker demonstrating task leasing with `TaskQueue<T>` plus wait groups and deferred cleanup.

### Run the worker sample with a local OpenTelemetry collector

1. Save the collector config below as `otel-collector.yaml` in the repository root:

     ```yaml
     receivers:
         otlp:
             protocols:
                 grpc:
                 http:

     exporters:
         logging:
             loglevel: info
         prometheus:
             endpoint: "0.0.0.0:9464"

     service:
         pipelines:
             metrics:
                 receivers: [otlp]
                 exporters: [prometheus, logging]
             traces:
                 receivers: [otlp]
                 exporters: [logging]
     ```

2. Start the collector (requires Docker):

     ```bash
     docker run --rm \
         -p 4317:4317 -p 4318:4318 -p 9464:9464 \
         -v "${PWD}/otel-collector.yaml:/etc/otelcol/config.yaml" \
         otel/opentelemetry-collector:0.103.1
     ```

3. In a second terminal, run the worker sample:

     ```bash
     dotnet run --project samples/Hugo.WorkerSample/Hugo.WorkerSample.csproj
     ```

     - Override the collector endpoint with `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` if you change ports.
     - Disable Prometheus scraping with `HUGO_PROMETHEUS_ENABLED=false` when running without the Prometheus exporter.

4. Inspect emitted metrics at `http://localhost:9464/metrics` and watch the collector logs for OTLP traces.

### Benchmarks

The `benchmarks/Hugo.Benchmarks` directory contains a comprehensive BenchmarkDotNet suite comparing Hugo primitives against native .NET equivalents:

- **Mutex vs SemaphoreSlim**: Lock acquisition and release performance
- **RwMutex**: Read/write lock contention scenarios
- **Once**: One-time initialization patterns
- **Pool**: Object pooling efficiency
- **Channel factories**: Bounded vs unbounded channel creation
- **Prioritized channels**: Multi-level priority queue throughput
- **Result pipelines**: Railway-oriented composition overhead
- **Select operations**: Channel coordination latency

Run benchmarks locally:

```bash
dotnet run --project benchmarks/Hugo.Benchmarks/Hugo.Benchmarks.csproj -c Release
```

Generated reports are available in `BenchmarkDotNet.Artifacts/results/`. Hugo primitives demonstrate competitive performance with native .NET types while providing additional safety guarantees and observability hooks.

## Native AOT

- Publish the sample worker to a native binary with `dotnet publish samples/Hugo.WorkerSample/Hugo.WorkerSample.csproj -c Release -r <rid> -p:PublishAot=true` (for example, `-r linux-x64` on CI).
- When persisting deterministic effects with custom payload types, register them with `JsonSerializerContext` / `JsonTypeInfoResolver` before publishing to trimming/AOT environments.

## Support & policies

- Questions & bugs: [GitHub issues](https://github.com/df49b9cd/Hugo/issues)
- Security disclosures: contact the maintainer privately before filing a public issue.
- Roadmap: keep up with priorities in [`docs/meta/roadmap.md`](docs/meta/roadmap.md).

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines on:

- Setting up your development environment
- Code style and conventions
- Running tests and collecting coverage
- Submitting pull requests
- Documentation updates

Quick start:

1. Install the .NET 10 SDK.
2. Run `dotnet build Hugo.slnx` and `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj` before submitting changes.
3. Collect coverage locally with `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj --collect:"XPlat Code Coverage"` to mirror CI.
4. Review [`docs/index.md`](docs/index.md) to update tutorials, how-to guides, and references when behaviour changes.
5. Track strategic work in [`docs/meta/roadmap.md`](docs/meta/roadmap.md) and ensure new APIs include XML docs plus cancellation coverage tests.

## License

Hugo is licensed under the [MIT License](LICENSE). You are free to use, modify, and distribute this library in accordance with the license terms.
