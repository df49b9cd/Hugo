# Samples & Benchmarks

The repository ships with runnable projects that demonstrate Hugo in real applications and measure performance against built-in .NET primitives.

## Worker Sample

Path: `samples/Hugo.WorkerSample`

```bash
dotnet run --project samples/Hugo.WorkerSample/Hugo.WorkerSample.csproj
```

Highlights:

- Demonstrates `WaitGroup` fan-out alongside channels inside a `BackgroundService` worker.
- Registers `TimeProvider` for deterministic scheduling during tests and propagates cancellation tokens end-to-end.
- Streams structured logs through `ILogger` so the worker can plug into observability pipelines.

## Benchmark Suite

Path: `benchmarks/Hugo.Benchmarks`

```bash
dotnet run -c Release --project benchmarks/Hugo.Benchmarks/Hugo.Benchmarks.csproj
```

Highlights:

- Uses BenchmarkDotNet to compare `Hugo.Mutex` against `SemaphoreSlim` under varying contention levels.
- Captures throughput and allocation data to validate the primitives under load.
- Writes detailed markdown, CSV, and JSON summaries under `BenchmarkDotNet.Artifacts/results` for further analysis.
