# Hugo.TaskQueues.Replication

This package exposes reusable helpers for replicating `TaskQueue<T>` and `SafeTaskQueue<T>` mutations:

- `TaskQueueReplicationSource` observes queue lifecycle events and emits transport-agnostic replication events.
- `CheckpointingTaskQueueReplicationSink` tracks per-peer/global checkpoints to guarantee idempotent application of replicated events.
- `TaskQueueDeterministicCoordinator` bridges replication streams with `DeterministicEffectStore` so side effects can be replayed safely.
- `TaskQueueReplicationJsonSerialization` exposes source-generated `JsonSerializerContext` helpers for replication event payloads.

Refer to `docs/reference/api-reference.md` and `docs/reference/distributed-task-leasing.md` for usage guidance.
