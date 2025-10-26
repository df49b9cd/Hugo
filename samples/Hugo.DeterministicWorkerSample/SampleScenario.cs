using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Publishes a scripted set of messages so the sample can demonstrate deterministic replay.
/// </summary>
sealed class SampleScenario(
    SimulatedKafkaTopic topic,
    PipelineEntityStore store,
    TimeProvider timeProvider,
    ILogger<SampleScenario> logger) : BackgroundService
{
    private readonly SimulatedKafkaTopic _topic = topic ?? throw new ArgumentNullException(nameof(topic));
    private readonly PipelineEntityStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<SampleScenario> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);

            DateTimeOffset now = _timeProvider.GetUtcNow();
            // Compose a deterministic script: two messages for the same entity, one for a second entity, then a replay.
            SimulatedKafkaMessage first = new("msg-001", "trend-042", 3.40, now);
            SimulatedKafkaMessage second = new("msg-002", "trend-042", 2.25, now.AddSeconds(1));
            SimulatedKafkaMessage third = new("msg-003", "trend-107", 6.80, now.AddSeconds(2));
            SimulatedKafkaMessage replay = first with { ObservedAt = now.AddSeconds(5) };

            SimulatedKafkaMessage[] script =
            [
                first,
                second,
                third,
                replay
            ];

            foreach (SimulatedKafkaMessage message in script)
            {
                await _topic.PublishAsync(message, stoppingToken).ConfigureAwait(false);
                // Log the synthetic publish so the console mirrors a Kafka producer.
                _logger.LogInformation(
                    "Published {MessageId} -> {EntityId} ({Amount:F2}) at {ObservedAt:o}",
                    message.MessageId,
                    message.EntityId,
                    message.Amount,
                    message.ObservedAt);
                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

            IReadOnlyList<PipelineEntity> snapshot = _store.Snapshot();
            // Display the final store state to prove replay didn't duplicate work.
            foreach (PipelineEntity entity in snapshot)
            {
                _logger.LogInformation(
                    "Store snapshot {EntityId}: total={Total:F2} average={Average:F2} processed={Processed}",
                    entity.EntityId,
                    entity.RunningTotal,
                    entity.RunningAverage,
                    entity.ProcessedCount);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host is shutting down
        }
    }
}
