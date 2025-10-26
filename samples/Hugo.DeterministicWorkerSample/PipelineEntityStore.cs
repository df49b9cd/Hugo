using System.Collections.Concurrent;

using Hugo;

/// <summary>
/// Provides an in-memory backing store plus helper models for saga-driven projections.
/// </summary>
sealed class PipelineEntityStore
{
    private readonly ConcurrentDictionary<string, PipelineEntity> _entities = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<Result<PipelineEntity>> LoadAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        cancellationToken.ThrowIfCancellationRequested();

        // Return the tracked entity if it exists; otherwise fabricate an empty projection.
        if (_entities.TryGetValue(entityId, out PipelineEntity? entity))
        {
            return ValueTask.FromResult(Result.Ok(entity));
        }

        return ValueTask.FromResult(Result.Ok(PipelineEntity.Create(entityId)));
    }

    public ValueTask<Result<PipelineEntity>> SaveAsync(PipelineEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();

        // Persist in-memory so subsequent loads and snapshots reflect the mutation.
        _entities[entity.EntityId] = entity;
        return ValueTask.FromResult(Result.Ok(entity));
    }

    public IReadOnlyList<PipelineEntity> Snapshot() =>
        // Produce a deterministic ordering so console output stays stable between runs.
        [.. _entities.Values.OrderBy(static e => e.EntityId, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// Represents the saga's materialized view (e.g., rolling totals for an entity).
/// </summary>
sealed record class PipelineEntity(
    string EntityId,
    double RunningTotal,
    int ProcessedCount,
    double LastAmount,
    DateTimeOffset LastUpdated)
{
    public double RunningAverage => ProcessedCount == 0 ? 0d : RunningTotal / ProcessedCount;

    public static PipelineEntity Create(string entityId) =>
        new(entityId, 0d, 0, 0d, DateTimeOffset.MinValue);
}

readonly record struct PipelineComputation(double Total, int ProcessedCount)
{
    public double Average => ProcessedCount == 0 ? 0d : Total / ProcessedCount;

    public static PipelineComputation Create(PipelineEntity entity, SimulatedKafkaMessage message)
    {
        // Increment aggregates with the latest observation; pure function keeps saga deterministic.
        double total = entity.RunningTotal + message.Amount;
        int processed = entity.ProcessedCount + 1;
        return new PipelineComputation(total, processed);
    }

    public PipelineEntity ApplyTo(PipelineEntity entity, SimulatedKafkaMessage message) =>
        // Emit a new immutable projection that carries the updated totals and last seen value.
        new(
            entity.EntityId,
            Total,
            ProcessedCount,
            message.Amount,
            message.ObservedAt);
}

/// <summary>
/// Returned to the worker so it can log whether the saga was replayed or newly executed.
/// </summary>
readonly record struct ProcessingOutcome(bool IsReplay, int Version, PipelineEntity Entity);
