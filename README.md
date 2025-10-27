# Hugo

[![CI](https://github.com/df49b9cd/Hugo/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/df49b9cd/Hugo/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Hugo.svg?logo=nuget)](https://www.nuget.org/packages/Hugo)
[![NuGet Diagnostics](https://img.shields.io/nuget/v/Hugo.Diagnostics.OpenTelemetry.svg?logo=nuget&label=Hugo.Diagnostics.OpenTelemetry)](https://www.nuget.org/packages/Hugo.Diagnostics.OpenTelemetry)

> Go-style concurrency primitives and functional result pipelines for .NET 9/10 applications.

## Table of contents

- [Highlights](#highlights)
- [When to use Hugo](#when-to-use-hugo)
- [Supported runtimes](#supported-runtimes)
- [Install](#install)
- [Quick start](#quick-start)
- [Observability in one call](#observability-in-one-call)
- [Documentation](#documentation)
- [Samples & benchmarks](#samples--benchmarks)
- [Project layout](#project-layout)
- [Support](#support)
- [Contributing](#contributing)
- [License](#license)

## Highlights

- **Deterministic concurrency**: Channels, wait groups, mutexes, RW locks, timers, defer, and select-style helpers mirror Go semantics while respecting `CancellationToken` and `TimeProvider`.
- **Railway-oriented results**: `Result<T>` pipelines keep success/failure explicit with `Then`, `Map`, `Recover`, `Ensure`, streaming helpers, sagas, and tiered fallbacks.
- **Observability first**: `GoDiagnostics` ships counters, histograms, and activity sources that plug directly into OpenTelemetry exporters or custom meter providers.
- **Source-generated error catalog**: Hugo’s well-known error codes are generated at compile time, ensuring consistent metadata for logs, metrics, and documentation.
- **Batteries included**: Prioritised channels, task queue leasing, retry policies, deterministic version gates, workflow telemetry, and profiling recipes ship alongside the core library.
- **Testability**: Fakeable time providers, deterministic effect capture, structured errors, and cancellation metadata keep async workflows reproducible in CI.

## When to use Hugo

- You need Go-like coordination primitives (channels/select/wait groups) without leaving C#.
- You want predictable error handling with structured metadata instead of exceptions for control flow.
- You are building workflow orchestrations that must replay deterministically across retries and recoveries.
- You plan to publish metrics and traces for concurrency-heavy workloads without custom instrumentation.
- You require reusable timeout/retry/cancellation playbooks that can be enforced across teams.

## Supported runtimes

- Targets `net9.0` and `net10.0`.
- Works with generic host builders, ASP.NET background services, worker services, and isolated Azure Functions workers.
- Verified with the .NET 10 preview SDK; install it alongside .NET 9 for the same coverage as CI.

## Install

```bash
dotnet add package Hugo
```

- Plays nicely with central package management (`Directory.Packages.props`).
- To iterate locally, run `dotnet pack src/Hugo/Hugo.csproj -o ./artifacts` and point a local feed at the generated `.nupkg`.

## Quick start

These steps mirror the [getting started tutorial](docs/tutorials/getting-started.md) but demonstrate channels, fan-in coordination, and `Result<T>` in a single console app.

1. **Bootstrap a sample**

    ```bash
    dotnet new console -n HugoQuickstart
    cd HugoQuickstart
    dotnet add package Hugo
    ```

2. **Compose channels, wait groups, and results**

    Replace `Program.cs` with:

    ```csharp
    using Hugo;
    using static Hugo.Go;

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    var metrics = MakeChannel<int>(capacity: 8);
    var jobs = MakeChannel<string>(capacity: 8);
    var merged = MakeChannel<object>(capacity: 16);
    var workers = new WaitGroup();

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

    var relay = FanInAsync(
        sources: new[] { metrics.Reader, jobs.Reader },
        destination: merged.Writer,
        completeDestination: true,
        cancellationToken: cts.Token);

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

    var fanInResult = await relay;
    if (fanInResult.IsFailure)
    {
        Console.WriteLine($"fan-in failed: {fanInResult.Error}");
    }

    await workers.WaitAsync(cts.Token);

    Console.WriteLine(string.Join(", ", messages));
    ```

3. **Run it**

    ```bash
    dotnet run
    ```

    Lower the timeout or cancel manually to see how `Error.Canceled` flows through the pipeline without throwing.

### Extend the sample

- Apply timeout, retry, and cancellation policies with the [playbook templates](docs/how-to/playbook-templates.md).
- Stream merged channel output into a background worker or `TaskQueue<T>` using [Coordinate fan-in workflows](docs/how-to/fan-in-channels.md).
- Swap in a fake `TimeProvider` to deterministically test deadlines and ticker behaviour.
- Wire observability with [Publish metrics and traces to OpenTelemetry](docs/how-to/observe-with-opentelemetry.md).

## Observability in one call

```csharp
using Hugo.Diagnostics.OpenTelemetry;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = builder.Environment.ApplicationName;
        options.OtlpEndpoint = new Uri("http://localhost:4317");
        options.AddPrometheusExporter = true;
    });

var app = builder.Build();
app.Run();
```

- Metrics: `waitgroup.*`, `channel.select.*`, `taskqueue.*`, `workflow.*`, `result.*`, and more (see [Diagnostics reference](docs/reference/diagnostics.md)).
- Traces: rate-limited spans that carry workflow metadata and channel coordination tags.
- Ready-made collector recipes live in the [worker sample walkthrough](#samples--benchmarks).

## Documentation

Hugo follows the Divio documentation system so you can find the right level of guidance quickly:

- **Tutorials** – step-by-step introductions such as [Getting started with channels and results](docs/tutorials/getting-started.md).
- **How-to guides** – task-oriented recipes for [fan-in coordination](docs/how-to/fan-in-channels.md), [OpenTelemetry export](docs/how-to/observe-with-opentelemetry.md), [workflow visibility](docs/how-to/workflow-visibility.md), profiling, and retry playbooks.
- **Reference** – definitive API docs covering [concurrency primitives](docs/reference/concurrency-primitives.md), [result pipelines](docs/reference/result-pipelines.md), [deterministic coordination](docs/reference/deterministic-coordination.md), [diagnostics](docs/reference/diagnostics.md), and the [full API catalogue](docs/reference/api-reference.md).
- **Explanation** – design trade-offs and rationale in [Design principles](docs/explanation/design-principles.md).
- **Meta** – roadmap, async audit tracker, and project direction in [`docs/meta`](docs/meta/).

Start at [`docs/index.md`](docs/index.md) for navigation and search hints.

## Samples & benchmarks

- `samples/Hugo.WorkerSample` – a background worker showcasing task queues, deterministic workflows, and OpenTelemetry integration.
- `benchmarks/Hugo.Benchmarks` – BenchmarkDotNet suites comparing Hugo primitives to baseline .NET constructs (mutex vs `SemaphoreSlim`, prioritized channels, result pipelines, select latency, object pools, timers).

Run the worker sample with a local collector:

```bash
docker run --rm \
    -p 4317:4317 -p 4318:4318 -p 9464:9464 \
    -v "${PWD}/otel-collector.yaml:/etc/otelcol/config.yaml" \
    otel/opentelemetry-collector:0.103.1

dotnet run --project samples/Hugo.WorkerSample/Hugo.WorkerSample.csproj
```

Benchmark results are written to `BenchmarkDotNet.Artifacts/results/`.

## Project layout

- `src/` – library source for concurrency primitives, result pipelines, policies, sagas, deterministic coordination, and diagnostics.
- `tests/` – unit, integration, and deterministic timing tests.
- `docs/` – tutorials, how-to guides, references, explanations, roadmap, and audit trackers (rendered via DocFX).
- `samples/` – runnable end-to-end scenarios.
- `benchmarks/` – performance harnesses executed with BenchmarkDotNet.
- `tools/` – profiling scripts, analyzers, and auxiliary automation.

## Support

- Questions & bugs: open an issue on [GitHub](https://github.com/df49b9cd/Hugo/issues).
- Security disclosures: contact the maintainer privately before filing a public issue.
- Roadmap & project status: follow [`docs/meta/roadmap.md`](docs/meta/roadmap.md).

## Contributing

- Review [CONTRIBUTING.md](CONTRIBUTING.md) for environment setup, coding standards, and workflow expectations.
- Run `dotnet build Hugo.slnx` and `dotnet test tests/Hugo.Tests/Hugo.Tests.csproj` before submitting a pull request.
- Collect coverage with `dotnet test --collect:"XPlat Code Coverage"` to match CI.
- Update tutorials/how-to/reference docs alongside code changes and log strategic work in [`docs/meta/roadmap.md`](docs/meta/roadmap.md).

## License

Hugo is licensed under the [MIT License](LICENSE). Use, modify, and distribute it within those terms.
