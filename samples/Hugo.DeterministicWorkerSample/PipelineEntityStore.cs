using System.Collections.Concurrent;

using Hugo;

/// <summary>
/// Provides an in-memory backing store plus helper models for saga-driven projections.
/// </summary>
sealed class PipelineEntityStore
{
    private readonly ConcurrentDictionary<string, PipelineEntity> _entities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the current projection for the supplied entity, creating a default snapshot when none exists.
    /// </summary>
    /// <param name="entityId">The entity identifier to load.</param>
    /// <param name="cancellationToken">Token used to cancel the load operation.</param>
    /// <returns>A deterministic result containing the entity projection.</returns>
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

    /// <summary>
    /// Persists the supplied projection so future loads and snapshots observe the mutation.
    /// </summary>
    /// <param name="entity">The entity projection to store.</param>
    /// <param name="cancellationToken">Token used to cancel the save operation.</param>
    /// <returns>A deterministic result containing the stored entity.</returns>
    public ValueTask<Result<PipelineEntity>> SaveAsync(PipelineEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();

        // Persist in-memory so subsequent loads and snapshots reflect the mutation.
        _entities[entity.EntityId] = entity;
        return ValueTask.FromResult(Result.Ok(entity));
    }

    /// <summary>
    /// Creates a deterministic snapshot of all tracked entities ordered by identifier.
    /// </summary>
    /// <returns>An immutable snapshot of the current projections.</returns>
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
    /// <summary>
    /// Gets the running average of all processed amounts for the entity.
    /// </summary>
    public double RunningAverage => ProcessedCount == 0 ? 0d : RunningTotal / ProcessedCount;

    /// <summary>
    /// Creates an empty projection for a newly observed entity.
    /// </summary>
    /// <param name="entityId">The entity identifier.</param>
    /// <returns>A pipeline entity with zeroed aggregates.</returns>
    public static PipelineEntity Create(string entityId) =>
        new(entityId, 0d, 0, 0d, DateTimeOffset.MinValue);
}

/// <summary>
/// Represents a computation performed over an entity as part of the saga.
/// </summary>
readonly record struct PipelineComputation(double Total, int ProcessedCount)
{
    /// <summary>
    /// Gets the average derived from the aggregated totals.
    /// </summary>
    public double Average => ProcessedCount == 0 ? 0d : Total / ProcessedCount;

    /// <summary>
    /// Calculates updated aggregates for the supplied entity and message.
    /// </summary>
    /// <param name="entity">The existing entity projection.</param>
    /// <param name="message">The message contributing to the aggregates.</param>
    /// <returns>The computed aggregate snapshot.</returns>
    public static PipelineComputation Create(PipelineEntity entity, SimulatedKafkaMessage message)
    {
        // Increment aggregates with the latest observation; pure function keeps saga deterministic.
        double total = entity.RunningTotal + message.Amount;
        int processed = entity.ProcessedCount + 1;
        return new PipelineComputation(total, processed);
    }

    /// <summary>
    /// Applies the computed aggregates to the supplied entity, producing an updated projection.
    /// </summary>
    /// <param name="entity">The original entity projection.</param>
    /// <param name="message">The message that produced the aggregates.</param>
    /// <returns>The updated entity projection.</returns>
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
