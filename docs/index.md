# Hugo Documentation

Hugo brings Go-style concurrency and result pipelines to .NET. This documentation follows the Divio system so you can quickly find the right kind of guidance for every development stage.

## Tutorials

- [Getting started with channels and results](tutorials/getting-started.md): build a minimal worker that writes to a channel, fans results back in, and handles failures with `Result<T>`.

## How-to Guides

- [Coordinate fan-in workflows](how-to/fan-in-channels.md): step-by-step recipe for merging multiple channel readers with cancellation and deadlines.
- [Publish metrics to OpenTelemetry](how-to/observe-with-opentelemetry.md): wire `GoDiagnostics` into your meter provider and export counters/histograms.

## Reference

- [Concurrency primitives](reference/concurrency-primitives.md): definitive description of `WaitGroup`, `Mutex`, `RwMutex`, timers, and select helpers.
- [Result pipeline APIs](reference/result-pipelines.md): catalogue of `Result<T>` operations, async overloads, and error metadata helpers.
- [Diagnostics surface](reference/diagnostics.md): instruments emitted by `GoDiagnostics` with units, dimensions, and usage notes.

## Explanation

- [Design principles](explanation/design-principles.md): rationale behind cancellation-first APIs, metadata-rich errors, and deterministic timing.

## Meta

- [Roadmap](meta/roadmap.md): active initiatives, recently shipped features, and contribution expectations.

For runnable demonstrations and performance data, see `samples/Hugo.WorkerSample` and `benchmarks/Hugo.Benchmarks`.
