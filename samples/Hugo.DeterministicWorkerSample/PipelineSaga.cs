using Hugo;
using Hugo.Sagas;

using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates load, compute, mutate, and persist steps for a single entity update.
/// </summary>
sealed class PipelineSaga(
    PipelineEntityStore store,
    ILogger<PipelineSaga> logger)
{
    private readonly PipelineEntityStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<PipelineSaga> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private static class SagaKeys
    {
        public const string Entity = "entity";
        public const string Computation = "computation";
        public const string Updated = "updated";
        public const string Persisted = "persisted";
        public const string PersistScope = "entity-save";
    }

    public async Task<Result<PipelineEntity>> ExecuteAsync(
        SimulatedKafkaMessage message,
        DeterministicGate.DeterministicWorkflowContext workflowContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(workflowContext);

        var builder = new ResultSagaBuilder();

        builder.AddStep(
            "load-entity",
            (stepContext, token) => _store.LoadAsync(message.EntityId, token),
            resultKey: SagaKeys.Entity);

        builder.AddStep(
            "calculate-next-state",
            (stepContext, _) =>
            {
                // Use the previously loaded entity to compute new aggregates.
                if (!stepContext.State.TryGet<PipelineEntity>(SagaKeys.Entity, out PipelineEntity entity))
                {
                    return ValueTask.FromResult(Result.Fail<PipelineComputation>(MissingSagaState(SagaKeys.Entity)));
                }

                PipelineComputation computation = PipelineComputation.Create(entity, message);
                return ValueTask.FromResult(Result.Ok(computation));
            },
            resultKey: SagaKeys.Computation);

        builder.AddStep(
            "apply-mutation",
            (stepContext, _) =>
            {
                // Apply the computed aggregates, producing the updated entity projection.
                if (!stepContext.State.TryGet<PipelineEntity>(SagaKeys.Entity, out PipelineEntity entity))
                {
                    return ValueTask.FromResult(Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Entity)));
                }

                if (!stepContext.State.TryGet<PipelineComputation>(SagaKeys.Computation, out PipelineComputation computation))
                {
                    return ValueTask.FromResult(Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Computation)));
                }

                PipelineEntity updated = computation.ApplyTo(entity, message);
                return ValueTask.FromResult(Result.Ok(updated));
            },
            resultKey: SagaKeys.Updated);

        builder.AddStep(
            "persist-entity",
            async (stepContext, token) =>
            {
                // Persist through the deterministic effect store so replays short-circuit here.
                if (!stepContext.State.TryGet<PipelineEntity>(SagaKeys.Updated, out PipelineEntity updated))
                {
                    return Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Updated));
                }

                Result<PipelineEntity> persisted = await workflowContext
                    .CaptureAsync(
                        SagaKeys.PersistScope,
                        async ct => await _store.SaveAsync(updated, ct).ConfigureAwait(false),
                        token)
                    .ConfigureAwait(false);

                if (persisted.IsSuccess)
                {
                    _logger.LogDebug(
                        "Persisted entity {EntityId} after message {MessageId} (version {Version})",
                        updated.EntityId,
                        message.MessageId,
                        workflowContext.Version);
                }

                return persisted;
            },
            resultKey: SagaKeys.Persisted);

        Result<ResultSagaState> sagaResult = await builder.ExecuteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (sagaResult.IsFailure)
        {
            return Result.Fail<PipelineEntity>(sagaResult.Error);
        }

        ResultSagaState state = sagaResult.Value;
        // The final persisted entity becomes the saga's output handed back to the processor.
        if (!state.TryGet<PipelineEntity>(SagaKeys.Persisted, out PipelineEntity entity))
        {
            return Result.Fail<PipelineEntity>(MissingSagaState(SagaKeys.Persisted));
        }

        return Result.Ok(entity);
    }

    private static Error MissingSagaState(string key) =>
        Error.From($"Saga state is missing '{key}'.", ErrorCodes.Validation);
}
