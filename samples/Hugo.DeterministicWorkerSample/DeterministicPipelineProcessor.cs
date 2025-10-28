using System.Diagnostics;

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

    /// <summary>
    /// Processes an incoming Kafka message inside a deterministic workflow, replaying persisted saga outcomes when available.
    /// </summary>
    /// <param name="message">The simulated Kafka message to process.</param>
    /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
    /// <returns>A <see cref="Result{ProcessingOutcome}"/> describing the pipeline outcome, including replay metadata.</returns>
    public async Task<Result<ProcessingOutcome>> ProcessAsync(SimulatedKafkaMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Each Kafka message becomes a unique deterministic workflow keyed by message id.
        string changeId = $"deterministic-pipeline::{message.MessageId}";
        const int Version = 1;

        using Activity? activity = DeterministicPipelineTelemetry.StartProcessingActivity(message);
        activity?.SetTag("sample.pipeline.change_id", changeId);

        Stopwatch stopwatch = Stopwatch.StartNew();

        Result<ProcessingOutcome> result = await _gate
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
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        if (result.IsSuccess)
        {
            ProcessingOutcome outcome = result.Value;
            DeterministicPipelineTelemetry.RecordProcessed(outcome.IsReplay, stopwatch.Elapsed);
            activity?.SetTag("sample.pipeline.is_replay", outcome.IsReplay);
            activity?.SetTag("sample.pipeline.version", outcome.Version);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            DeterministicPipelineTelemetry.RecordFailure();
            if (result.Error is { } error)
            {
                activity?.SetStatus(ActivityStatusCode.Error, error.Message);
                if (!string.IsNullOrWhiteSpace(error.Code))
                {
                    activity?.SetTag("error.code", error.Code);
                }

                activity?.SetTag("error.message", error.Message);
            }
        }

        return result;
    }
}
