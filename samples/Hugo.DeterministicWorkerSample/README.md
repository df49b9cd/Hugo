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
