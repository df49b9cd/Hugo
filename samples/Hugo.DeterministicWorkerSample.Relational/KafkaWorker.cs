using System.Threading.Channels;

using Hugo;
using Hugo.DeterministicWorkerSample.Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Streams messages from the simulated topic into the deterministic pipeline and logs outcomes.
/// </summary>
sealed class KafkaWorker(
    SimulatedKafkaTopic topic,
    DeterministicPipelineProcessor processor,
    ILogger<KafkaWorker> logger) : BackgroundService
{
    private readonly SimulatedKafkaTopic _topic = topic ?? throw new ArgumentNullException(nameof(topic));
    private readonly DeterministicPipelineProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly ILogger<KafkaWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Continuously reads messages from the simulated topic and hands them to the deterministic processor.
    /// </summary>
    /// <param name="stoppingToken">Token used to stop the background worker.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ChannelReader<SimulatedKafkaMessage> reader = _topic.Reader;

        // Continuously drain the topic, handing each message to the deterministic processor.
        await foreach (SimulatedKafkaMessage message in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            Result<ProcessingOutcome> result = await _processor.ProcessAsync(message, stoppingToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                Error error = result.Error ?? Error.Unspecified();
                _logger.LogError(
                    "Failed to process message {MessageId} for {EntityId}: {ErrorCode} - {ErrorMessage}",
                    message.MessageId,
                    message.EntityId,
                    error.Code ?? ErrorCodes.Unspecified,
                    error.Message);
                continue;
            }

            ProcessingOutcome outcome = result.Value;
            if (outcome.IsReplay)
            {
                // DeterministicGate indicated the work already ran, so we surface the replayed payload.
                _logger.LogInformation(
                    "Replayed {MessageId} (version {Version}) for {EntityId}: total={Total:F2} average={Average:F2} processed={Processed}",
                    message.MessageId,
                    outcome.Version,
                    outcome.Entity.EntityId,
                    outcome.Entity.RunningTotal,
                    outcome.Entity.RunningAverage,
                    outcome.Entity.ProcessedCount);
            }
            else
            {
                // First-run success: log the latest aggregate snapshot.
                _logger.LogInformation(
                    "Processed {MessageId} for {EntityId}: total={Total:F2} average={Average:F2} processed={Processed}",
                    message.MessageId,
                    outcome.Entity.EntityId,
                    outcome.Entity.RunningTotal,
                    outcome.Entity.RunningAverage,
                    outcome.Entity.ProcessedCount);
            }
        }
    }
}
