# Hugo.DeterministicWorkerSample

This sample shows how to build an always-on worker that processes Kafka-like traffic while remaining replay-safe. It uses Hugo’s deterministic coordination primitives to prove that long-running sagas can survive retries, duplicates, and out-of-order deliveries without corrupting state or double-writing downstream systems.

## Learning Goals

- Understand how `DeterministicGate`, `VersionGate`, and `DeterministicEffectStore` cooperate to make message handling idempotent.
- See a practical saga that isolates computation, mutation, and persistence while remaining deterministic.
- Observe end-to-end telemetry emitted by a deterministic workflow (OpenTelemetry spans, counters, and histograms).
- Explore strategies for extending the sample to real Kafka partitions, different entity models, or external data stores.

## Prerequisites

- .NET 8 SDK or later.
- A terminal that can run `dotnet` CLI.
- (Optional) An OpenTelemetry collector or Prometheus endpoint if you want to stream metrics and traces.

Everything else is self-contained: the sample emulates Kafka with an in-process channel so no brokers or databases are required.

## Quick Start

From the repository root, run:

```bash
dotnet run --project samples/Hugo.DeterministicWorkerSample/Hugo.DeterministicWorkerSample.csproj
```

The host starts two background services:

1. `SampleScenario` publishes patterned messages into `SimulatedKafkaTopic`.
2. `KafkaWorker` consumes the messages and hands them to `DeterministicPipelineProcessor`.

Stop the sample with `Ctrl+C`. Logs reveal whether each message was processed live or replayed from deterministic storage, and every tenth iteration prints aggregate snapshots for each entity.

Environment variables accepted by the sample:

- `Telemetry:ConsoleExporterEnabled` – toggles console metric/trace exporters (default: `true`).
- `Telemetry:OtlpExporterEnabled` or `OTEL_EXPORTER_OTLP_ENDPOINT` – enables OTLP exporters and sets the target endpoint.
- `Telemetry:PrometheusExporterEnabled` or `HUGO_PROMETHEUS_ENABLED` – exposes metrics through the Prometheus exporter.

## Scenario Overview

`SampleScenario` imitates a noisy Kafka producer:

- **Sequential traffic** – steady stream of `msg-{N}` events keeps entity aggregates moving forward.
- **Out-of-order traffic** – occasional `msg-oo-{N}` messages arrive with back-dated timestamps to mimic late data.
- **Replays** – every fifth iteration resubmits a previously seen `messageId`, checking that deterministic storage prevents duplicate work.
- **Bursts** – randomly tagged `msg-burst-{N}` messages create load spikes on different entities.

The producer chooses from four entity identifiers (`trend-042`, `trend-107`, `trend-204`, `trend-314`). Each message carries an amount to aggregate and an `ObservedAt` timestamp that feeds the running totals.

## Component Map

| Component | File | Responsibility |
| --- | --- | --- |
| Program | `Program.cs` | Builds the host, configures logging, deterministic stores, and OpenTelemetry instrumentation. |
| Producer | `SampleScenario.cs` | Publishes the scripted message mix and prints projection snapshots. |
| Topic | `SimulatedKafkaTopic.cs` | In-memory single-partition queue (`Channel<T>`) that stands in for Kafka. |
| Worker | `KafkaWorker.cs` | Reads messages, forwards them to the processor, and logs replay vs. first-run. |
| Processor | `DeterministicPipelineProcessor.cs` | Wraps saga execution in a deterministic workflow and surfaces `ProcessingOutcome`. |
| Saga | `PipelineSaga.cs` | Performs load → compute → mutate → persist using `ResultSagaBuilder`. |
| Store | `PipelineEntityStore.cs` | In-memory projection backing store plus helper records for entities and computations. |
| Telemetry | `DeterministicPipelineTelemetry.cs` | Defines the sample’s `ActivitySource`, `Meter`, and metric instruments. |
| Serialization | `DeterministicPipelineSerializerContext.cs` | Source-generated metadata so deterministic stores can serialize pipeline types. |

## Deterministic Execution Flow

1. **Message ingress** – `SampleScenario` creates a `SimulatedKafkaMessage` and calls `SimulatedKafkaTopic.PublishAsync`. The topic writes the message into an unbounded single-reader channel.
2. **Queue consumption** – `KafkaWorker` awaits `ReadAllAsync` and passes each message to `DeterministicPipelineProcessor.ProcessAsync`.
3. **Workflow gating** – The processor builds a deterministic workflow keyed by `deterministic-pipeline::{MessageId}`. Version `1` is registered in both `VersionGate` and `DeterministicEffectStore`.
4. **Replay check** – If the same `messageId` already ran, the gate short-circuits and returns the previously captured effect (`PipelineEntity`), skipping saga execution.
5. **Saga execution** – First-run messages execute these steps inside the workflow context:
   - Load or create the current `PipelineEntity` projection.
   - Compute new aggregates (`PipelineComputation`) using the incoming amount.
   - Apply the aggregates to produce an updated `PipelineEntity`.
   - Persist the updated entity through `DeterministicWorkflowContext.CaptureAsync`. This call stores the effect in `DeterministicEffectStore`, guaranteeing the same payload is replayed next time.
6. **Outcome logging & metrics** – The processor records counters/histograms, annotates the current `Activity`, and hands a `ProcessingOutcome` back to the worker. The worker logs whether it observed a replay.
7. **Snapshots** – Every tenth iteration `SampleScenario` prints the in-memory projection list so you can verify aggregates remain stable despite duplicates and reordering.

### Guarantees Demonstrated

- **Replay safety** – Duplicate `messageId`s never re-run the saga; they surface the cached effect.
- **Idempotent persistence** – Only the first execution mutates the store, preventing double writes.
- **Deterministic serialization** – Shared `JsonSerializerOptions` ensure deterministic metadata for both workflow versions and captured effects.
- **Time-stable projections** – Out-of-order timestamps update `LastUpdated` without corrupting prior aggregates.

## Observability and Tuning

- Traces originate from `DeterministicPipelineTelemetry.ActivitySource` and use consumer semantics (`messaging.*` tags).
- Metrics include:
  - `sample.pipeline.messages.processed` – total processed messages.
  - `sample.pipeline.messages.replayed` – count of replayed workflows.
  - `sample.pipeline.messages.failed` – workflow failures.
  - `sample.pipeline.processing.duration` – histogram of saga execution time in milliseconds.
- Toggle exporters via configuration or environment variables (see **Quick Start**). When Prometheus is enabled, scrape the default port exposed by Hugo’s telemetry host.
- To point at an OTLP collector: `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run ...`

## Experimentation Guide

- **Inject manual messages** – Set a breakpoint or use a REPL to call `SimulatedKafkaTopic.PublishAsync(SimulatedKafkaMessage.Create(...))`. Reusing the same `MessageId` demonstrates replay.
- **Alter message patterns** – Modify `SampleScenario.ExecuteAsync` to change frequencies, introduce partitions, or simulate failures.
- **Test failure paths** – Throw from `PipelineSaga` steps or `PipelineEntityStore.SaveAsync` to see how deterministic retries behave.
- **Change the saga schema** – Introduce new fields to `PipelineEntity` and bump the workflow version in `DeterministicPipelineProcessor` to practice rolling upgrades.
- **Externalize persistence** – Replace `PipelineEntityStore` with a database-backed projection. Only the deterministic capture needs to remain pure (idempotent write returning the same payload).

## Troubleshooting

- **Nothing happens after startup** – Ensure the worker is not blocked by a debugger. The producer waits ~250 ms before publishing the first message.
- **No telemetry is emitted** – Confirm `Telemetry:ConsoleExporterEnabled` is `true` or configure OTLP/Prometheus exporters.
- **Unexpected replays** – Check that the message ID is unique per logical delivery. Deterministic workflows use `messageId` as the change identifier.
- **Version conflicts** – When you change the saga shape, increment the workflow version to avoid reading stale effects.
- **Channel overflow concerns** – The simulated topic is unbounded; if you rework it to be bounded, ensure producers handle `ChannelFullMode` appropriately.

## Extending to Real Deployments

- Swap `SimulatedKafkaTopic` for a `Confluent.Kafka` consumer or another queue; keep the processor interface the same.
- Persist entities to external storage (SQL, Redis, Cosmos DB) by implementing deterministic capture with idempotent writes.
- Partition workloads by feeding multiple workers with distinct `SimulatedKafkaTopic` instances or keyed partitions.
- Integrate with your observability stack by adjusting `DeterministicPipelineTelemetry` or adding richer activity attributes.

## Additional Resources

- `README.md` in the repository root for a broader overview of Hugo.
- Hugo deterministic coordination docs (<https://github.com/df49b9cd/hugo>) for API reference and design rationale.
- OpenTelemetry .NET documentation for exporter configuration specifics.

Feel free to tailor the scenario to your domain—swap out the entity model, connect to real infrastructure, or add compensating actions. The deterministic workflow ensures that, regardless of delivery quirks, the saga stays correct and replay-proof.
