namespace Hugo.DeterministicWorkerSample.Core;

/// <summary>
/// Abstraction over the projection datastore used by the pipeline saga.
/// </summary>
public interface IPipelineEntityRepository
{
    /// <summary>
    /// Loads the current projection for the supplied entity, returning a deterministic result.
    /// </summary>
    /// <param name="entityId">Identifier of the entity to load.</param>
    /// <param name="cancellationToken">Token used to cancel the load.</param>
    /// <returns>A deterministic result containing the entity projection.</returns>
    ValueTask<Result<PipelineEntity>> LoadAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the supplied projection so future loads observe the mutation.
    /// </summary>
    /// <param name="entity">The entity projection to store.</param>
    /// <param name="cancellationToken">Token used to cancel the save.</param>
    /// <returns>A deterministic result containing the stored entity.</returns>
    ValueTask<Result<PipelineEntity>> SaveAsync(PipelineEntity entity, CancellationToken cancellationToken);

    /// <summary>
    /// Produces a snapshot of the currently tracked projections for diagnostics.
    /// </summary>
    /// <returns>A deterministic snapshot of all tracked entities.</returns>
    IReadOnlyList<PipelineEntity> Snapshot();
}

/// <summary>
/// Represents the saga's materialized view (e.g., rolling totals for an entity).
/// </summary>
public sealed record class PipelineEntity(
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
public readonly record struct PipelineComputation(double Total, int ProcessedCount)
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
public readonly record struct ProcessingOutcome(bool IsReplay, int Version, PipelineEntity Entity);
