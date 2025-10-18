# Samples & Benchmarks

The repository ships with runnable projects that demonstrate Hugo in real applications and measure performance against built-in .NET primitives.

## Worker Sample

Path: `samples/Hugo.WorkerSample`

```bash
cd samples/Hugo.WorkerSample
dotnet run
```

Highlights:

- Demonstrates `WaitGroup` fan-out alongside channels inside a `BackgroundService` worker.
- Uses `Go.Defer` for structured cleanup, deterministic `TimeProvider` injection, and cancellation-first patterns.
- Streams structured logs through `ILogger` so the worker can plug into observability pipelines.

## Benchmark Suite

Path: `benchmarks/Hugo.Benchmarks`

```bash
cd benchmarks/Hugo.Benchmarks
DOTNET_ENVIRONMENT=Development dotnet run -c Release
```

Highlights:

- Uses BenchmarkDotNet to compare `Hugo.Mutex` against `SemaphoreSlim` under varying contention levels.
- Captures throughput and allocation data to validate the primitives under load.
- Provides an extensible scaffold to add new microbenchmarks as additional primitives land.
