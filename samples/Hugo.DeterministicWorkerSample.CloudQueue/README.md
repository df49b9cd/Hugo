# Hugo.DeterministicWorkerSample.CloudQueue

This sample demonstrates deterministic processing against cloud infrastructure. Messages arrive through Azure Queue Storage, deterministic state lives in Azure Cosmos DB, and the saga projection is stored in a Cosmos container. The pipeline reuses the same deterministic workflow core shared by the other samples.

## What It Shows

- `QueuePublisher` pushes patterned traffic into an Azure queue (works against Azurite or the real service).
- `QueueWorker` polls the queue, deserialises `SimulatedKafkaMessage` payloads, and hands them to `DeterministicPipelineProcessor`.
- `Hugo.Deterministic.Cosmos` implements the deterministic state store so duplicate deliveries short-circuit.
- `CosmosPipelineEntityRepository` persists the saga projection in a separate container and supports snapshot inspection.

## Prerequisites

- .NET 10 SDK.
- Azure Storage queue connection string (e.g., `UseDevelopmentStorage=true` for Azurite).
- Azure Cosmos DB account or emulator connection string.
- Optional: OpenTelemetry collector or Prometheus endpoint for telemetry.

## Configuration

The sample reads configuration in the following order:

- Queue connection string: `Queue:ConnectionString`, `ConnectionStrings:Queue`, or environment variable `HUGO_SAMPLE_QUEUE` (defaults to `UseDevelopmentStorage=true`).
- Queue name: `Queue:Name` (default `deterministic-events`).
- Cosmos connection string: `Cosmos:ConnectionString` or environment variable `HUGO_SAMPLE_COSMOS` (defaults to the emulator endpoint).
- Cosmos database/container names:
  - `Cosmos:Database` (default `hugo-deterministic`).
  - `Cosmos:DeterministicContainer` for deterministic effects (default `deterministic-effects`).
  - `Cosmos:PipelineContainer` for saga projections (default `pipeline-projections`).

## Running the Sample

```bash
 dotnet run --project samples/Hugo.DeterministicWorkerSample.CloudQueue/Hugo.DeterministicWorkerSample.CloudQueue.csproj
```

On startup the worker will create the queue and Cosmos containers if they do not already exist (configurable via `Cosmos:CreateResources`). Queue messages use JSON payloads serialised by `QueueMessageSerializer`.

## Flow

1. `QueuePublisher` periodically enqueues sequential, out-of-order, replay, and burst messages.
2. `QueueWorker` receives batches (up to 16 messages), processes each inside a deterministic workflow, and deletes successful deliveries.
3. On failure the queue message visibility is shortened so it can be retried; deterministic capture ensures replays reuse the previously captured effect.
4. `CosmosPipelineEntityRepository` stores projections by `entityId` and supports snapshots for diagnostics.

## Extending the Sample

- Point the queue at a production Azure Storage account and configure managed identity/Shared Access Signatures instead of connection strings.
- Adjust queue batch sizes or visibility timeouts to match production retry semantics.
- Turn on OTLP/Prometheus exporters to stream telemetry out of the sample.
- Replace `QueuePublisher` with a real event source (e.g., IoT Hub, Event Grid) and observe that deterministic processing stays consistent.

## Troubleshooting

- **Queue messages never arrive** – Confirm the queue exists (`QueueClient.CreateIfNotExistsAsync`) and your connection string targets the correct account.
- **Cosmos throughput exceptions** – Increase RU/s or throttle the publisher loop; the sample publishes aggressively by default.
- **Deserialisation failures** – Messages must remain JSON-compatible with `SimulatedKafkaMessage`; inspect warning logs for discarded payloads.

See `docs/deterministic-persistence-providers.md` for storage-specific guidance and the relational sample for SQL Server integration.
