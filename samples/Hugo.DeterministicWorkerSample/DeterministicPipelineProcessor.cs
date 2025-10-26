using Hugo;

using Microsoft.Extensions.Logging;

/// <summary>
/// Coordinates deterministic execution by dispatching saga work inside a workflow gate.
/// </summary>
sealed class DeterministicPipelineProcessor(
    DeterministicGate gate,
    PipelineSaga saga,
    ILogger<DeterministicPipelineProcessor> logger)
{
    private readonly DeterministicGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly PipelineSaga _saga = saga ?? throw new ArgumentNullException(nameof(saga));
    private readonly ILogger<DeterministicPipelineProcessor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<Result<ProcessingOutcome>> ProcessAsync(SimulatedKafkaMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Each Kafka message becomes a unique deterministic workflow keyed by message id.
        string changeId = $"deterministic-pipeline::{message.MessageId}";
        const int Version = 1;

        return _gate
            .Workflow<ProcessingOutcome>(changeId, Version, Version)
            .ForVersion(
                Version,
                async (context, ct) =>
                {
                    // Run the saga inside the gate so its effects are captured and replay-safe.
                    Result<PipelineEntity> sagaResult = await _saga.ExecuteAsync(message, context, ct).ConfigureAwait(false);
                    if (sagaResult.IsFailure)
                    {
                        Error error = sagaResult.Error ?? Error.Unspecified();
                        _logger.LogWarning(
                            "Saga failed for {MessageId}: {Error}",
                            message.MessageId,
                            error.Message);
                        return Result.Fail<ProcessingOutcome>(error);
                    }

                    PipelineEntity entity = sagaResult.Value;
                    // Deterministic outcomes surface whether this was a first-run or replay.
                    return Result.Ok(new ProcessingOutcome(!context.IsNew, context.Version, entity));
                })
            .ExecuteAsync(cancellationToken);
    }
}
