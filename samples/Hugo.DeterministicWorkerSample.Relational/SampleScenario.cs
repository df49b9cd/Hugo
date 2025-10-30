using Hugo.DeterministicWorkerSample.Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Continuously publishes simulated Kafka messages covering sequential, replay, and out-of-order scenarios.
/// </summary>
sealed class SampleScenario(
    SimulatedKafkaTopic topic,
    IPipelineEntityRepository store,
    TimeProvider timeProvider,
    ILogger<SampleScenario> logger) : BackgroundService
{
    private readonly SimulatedKafkaTopic _topic = topic ?? throw new ArgumentNullException(nameof(topic));
    private readonly IPipelineEntityRepository _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<SampleScenario> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private static readonly string[] EntityIds = ["trend-042", "trend-107", "trend-204", "trend-314"];

    /// <summary>
    /// Publishes patterned messages to the simulated topic until the host shuts down.
    /// </summary>
    /// <param name="stoppingToken">Token used to stop the background worker.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);

            var random = new Random(42);
            var publishedHistory = new List<SimulatedKafkaMessage>(capacity: 32);
            int iteration = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                iteration++;
                DateTimeOffset now = _timeProvider.GetUtcNow();

                // Primary sequential message.
                string entityId = EntityIds[iteration % EntityIds.Length];
                double amount = Math.Round(random.NextDouble() * 5 + 1.25, 2);
                SimulatedKafkaMessage sequential = new($"msg-{iteration:000000}", entityId, amount, now);
                await PublishAsync(sequential, "sequential", stoppingToken).ConfigureAwait(false);
                publishedHistory.Add(sequential);

                // Simulate out-of-order delivery by backdating the observed time occasionally.
                if (iteration % 3 == 0)
                {
                    DateTimeOffset olderTimestamp = now.AddSeconds(-random.Next(1, 4));
                    double outOfOrderAmount = Math.Round(random.NextDouble() * 4 + 0.75, 2);
                    SimulatedKafkaMessage outOfOrder = new($"msg-oo-{iteration:000000}", entityId, outOfOrderAmount, olderTimestamp);
                    await PublishAsync(outOfOrder, "out-of-order", stoppingToken).ConfigureAwait(false);
                    publishedHistory.Add(outOfOrder);
                }

                // Replays resend a previously published message id with a new observed time.
                if (iteration % 5 == 0 && publishedHistory.Count > 0)
                {
                    SimulatedKafkaMessage replaySource = publishedHistory[random.Next(publishedHistory.Count)];
                    SimulatedKafkaMessage replay = replaySource with { ObservedAt = now.AddSeconds(random.Next(1, 4)) };
                    await PublishAsync(replay, "replay", stoppingToken).ConfigureAwait(false);
                }

                // Burst traffic for a different entity to mimic mixed workloads.
                if (iteration % 7 == 0)
                {
                    string burstEntity = EntityIds[random.Next(EntityIds.Length)];
                    double burstAmount = Math.Round(random.NextDouble() * 10 + 2.0, 2);
                    DateTimeOffset burstTimestamp = now.AddMilliseconds(random.Next(-750, 750));
                    SimulatedKafkaMessage burst = new($"msg-burst-{iteration:000000}", burstEntity, burstAmount, burstTimestamp);
                    await PublishAsync(burst, "burst", stoppingToken).ConfigureAwait(false);
                    publishedHistory.Add(burst);
                }

                if (iteration % 10 == 0)
                {
                    IReadOnlyList<PipelineEntity> snapshot = _store.Snapshot();
                    foreach (PipelineEntity entity in snapshot)
                    {
                        _logger.LogInformation(
                            "Snapshot {EntityId}: total={Total:F2} average={Average:F2} processed={Processed}",
                            entity.EntityId,
                            entity.RunningTotal,
                            entity.RunningAverage,
                            entity.ProcessedCount);
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host is shutting down
        }
    }

    /// <summary>
    /// Publishes a message to the simulated topic and logs the scenario tag.
    /// </summary>
    /// <param name="message">The message being published.</param>
    /// <param name="scenario">The scenario label included in the log output.</param>
    /// <param name="cancellationToken">Token used to cancel the publish operation.</param>
    private async Task PublishAsync(SimulatedKafkaMessage message, string scenario, CancellationToken cancellationToken)
    {
        await _topic.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "[{Scenario}] Published {MessageId} -> {EntityId} ({Amount:F2}) at {ObservedAt:o}",
            scenario,
            message.MessageId,
            message.EntityId,
            message.Amount,
            message.ObservedAt);
    }
}
