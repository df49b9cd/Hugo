using System.Collections.Concurrent;

using Hugo;
using Hugo.DeterministicWorkerSample.Core;

/// <summary>
/// Provides an in-memory backing store plus helper models for saga-driven projections.
/// </summary>
sealed class InMemoryPipelineEntityRepository : IPipelineEntityRepository
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
