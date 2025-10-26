# Hugo.DeterministicWorkerSample

This sample hosts a background worker that reacts to messages published to a simulated Kafka topic. It demonstrates how Hugo’s deterministic coordination APIs can be applied to a long-running service that must remain replay-safe even when messages arrive out of order, are duplicated, or are retried.

## Architecture Overview

- **Host (`Program.cs`)** – Boots a generic host, sets up logging, configures Hugo’s deterministic services (`VersionGate`, `DeterministicEffectStore`, `DeterministicGate`), and wires OpenTelemetry instrumentation for the sample-specific `ActivitySource`/`Meter`.
- **Producer (`SampleScenario`)** – A `BackgroundService` that continuously enqueues messages into `SimulatedKafkaTopic`, producing sequential events, bursts, deliberate replays, and out-of-order timestamps across multiple entity ids.
- **Consumer (`KafkaWorker`)** – Reads from the topic and hands each message to `DeterministicPipelineProcessor`, logging whether processing ran live or was replayed from persisted effects.
- **Processing pipeline (`DeterministicPipelineProcessor`)** – Wraps saga execution in a Hugo deterministic workflow. Each Kafka message becomes a unique change-id so replayed deliveries return the previously captured result instead of re-running the saga.
- **Saga (`PipelineSaga`)** – Orchestrates the read–compute–mutate–persist steps using `ResultSagaBuilder`, keeping step results inside `ResultSagaState` so each phase can fail independently and provide compensations if needed.
- **State store (`PipelineEntityStore`)** – An in-memory projection store that behaves like an operational datastore; it supports loading/creating entities, applying mutated state, and taking snapshots for diagnostics.
- **Deterministic persistence** – `VersionGate` records which workflow version executed, while `DeterministicEffectStore` captures the saga’s terminal effect (persisted entity). Replays skip the mutation path and return the captured effect, guaranteeing idempotent outcomes.

## Data Pipeline

1. **Message ingress** – `SampleScenario` emits a `SimulatedKafkaMessage` onto `SimulatedKafkaTopic`. Each message contains a message id, entity id, observed amount, and timestamp.
2. **Queue consumption** – `KafkaWorker` pulls messages serially and calls `DeterministicPipelineProcessor.ProcessAsync`.
3. **Workflow gating** – The processor invokes `DeterministicGate.Workflow(...).ExecuteAsync`, creating a deterministic scope keyed by `deterministic-pipeline::{messageId}`. The gate checks `VersionGate` and replays cached effects when the message id was seen before.
4. **Saga execution**  
   - *Load step* – Retrieve or materialize the entity from `PipelineEntityStore`.  
   - *Computation step* – Calculate new totals and averages based on the incoming amount.  
   - *Mutation step* – Apply the computation to produce an updated entity snapshot.  
   - *Persist step* – Via `DeterministicWorkflowContext.CaptureAsync`, persist the updated entity through `PipelineEntityStore` using `DeterministicEffectStore`, ensuring the same effect is returned on replay.
5. **Telemetry + logging** – The processor records activity spans/counters (processed, replayed, failed) via `DeterministicPipelineTelemetry`. `KafkaWorker` logs whether the outcome was a replay or a fresh execution.
6. **Observation** – Every ten iterations the scenario prints store snapshots so you can see aggregates evolving without duplication, even though the input feeds duplicates and out-of-order data.

## Deterministic Guarantees

- **Replay safety** – Re-enqueuing the same `messageId` returns the cached result from `DeterministicEffectStore` without re-running the saga logic.
- **Idempotent persistence** – Store writes are captured as deterministic effects; only the first successful execution performs the mutation.
- **Time-coherent processing** – Out-of-order messages still mutate the entity with their own timestamp, demonstrating that deterministic capture addresses repeatability while higher-level ordering logic can be layered in if desired.

## Telemetry

- Tracing and metrics are emitted via `DeterministicPipelineTelemetry` (`ActivitySource` + `Meter`).  
- Console exporters are enabled by default for quick inspection.  
- Set `OTEL_EXPORTER_OTLP_ENDPOINT` to forward data to an OTLP collector.  
- Enable the Prometheus exporter by setting `Telemetry:PrometheusExporterEnabled=true` (or `HUGO_PROMETHEUS_ENABLED=true`).  
- Exporters can be toggled individually with:
  - `Telemetry:ConsoleExporterEnabled`
  - `Telemetry:OtlpExporterEnabled`

## Running the Sample

From the repository root:

```bash
dotnet run --project samples/Hugo.DeterministicWorkerSample/Hugo.DeterministicWorkerSample.csproj
```

The scenario runs until you stop the host (`Ctrl+C`). Feel free to modify `SampleScenario` to introduce new patterns (e.g., batching or partitions) and observe how deterministic workflows preserve correctness even as the delivery model changes. To experiment interactively, call `SimulatedKafkaTopic.PublishAsync` in a debugger or REPL to inject additional messages and watch the replay logic respond. 
