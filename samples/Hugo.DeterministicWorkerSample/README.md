# Hugo.DeterministicWorkerSample

This sample hosts a background worker that reacts to messages published to a simulated Kafka topic. Each message drives a saga that:

- loads an entity from the in-memory store,
- computes the next aggregate values,
- applies the mutation, and
- persists the update through the deterministic effect store.

The `DeterministicGate` and `InMemoryDeterministicStateStore` ensure that every change is captured exactly once. If a message is replayed (same message id), the stored effect is returned without re-running the saga so the pipeline remains deterministic.

To run the sample from the repository root:

```bash
dotnet run --project samples/Hugo.DeterministicWorkerSample/Hugo.DeterministicWorkerSample.csproj
```

Publish additional messages by calling `SimulatedKafkaTopic.PublishAsync`. Re-enqueueing the same message id demonstrates the deterministic replay path logged by `KafkaWorker`.

Telemetry is wired with OpenTelemetry: the worker emits traces/metrics via `DeterministicPipelineTelemetry`. Console exporters are enabled by default; point `OTEL_EXPORTER_OTLP_ENDPOINT` to send data to an OTLP collector, or enable the Prometheus exporter by setting `HUGO_PROMETHEUS_ENABLED=true` in `appsettings` or the environment.

`SampleScenario` runs continuously, mixing sequential updates, replays, out-of-order timestamps, and bursts across multiple entities so the deterministic pipeline can be observed under varied conditions. Stop the host (`Ctrl+C`) to halt message production.
