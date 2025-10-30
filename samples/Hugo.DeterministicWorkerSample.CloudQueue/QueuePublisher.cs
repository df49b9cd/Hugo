using System;
using System.Collections.Generic;

using Azure.Storage.Queues;

using Hugo.DeterministicWorkerSample.Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hugo.DeterministicWorkerSample.CloudQueue;

/// <summary>
/// Publishes patterned messages into an Azure Storage queue so the worker can consume them.
/// </summary>
internal sealed class QueuePublisher(
    QueueClient queueClient,
    TimeProvider timeProvider,
    ILogger<QueuePublisher> logger) : BackgroundService
{
    private readonly QueueClient _queueClient = queueClient ?? throw new ArgumentNullException(nameof(queueClient));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<QueuePublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private static readonly string[] EntityIds = ["trend-042", "trend-107", "trend-204", "trend-314"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _queueClient.CreateIfNotExistsAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Queue publisher targeting {QueueName}.", _queueClient.Name);

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

                string entityId = EntityIds[iteration % EntityIds.Length];
                double amount = Math.Round(random.NextDouble() * 5 + 1.25, 2);
                SimulatedKafkaMessage sequential = new($"msg-{iteration:000000}", entityId, amount, now);
                await EnqueueAsync(sequential, "sequential", stoppingToken).ConfigureAwait(false);
                publishedHistory.Add(sequential);

                if (iteration % 3 == 0)
                {
                    DateTimeOffset olderTimestamp = now.AddSeconds(-random.Next(1, 4));
                    double outOfOrderAmount = Math.Round(random.NextDouble() * 4 + 0.75, 2);
                    SimulatedKafkaMessage outOfOrder = new($"msg-oo-{iteration:000000}", entityId, outOfOrderAmount, olderTimestamp);
                    await EnqueueAsync(outOfOrder, "out-of-order", stoppingToken).ConfigureAwait(false);
                    publishedHistory.Add(outOfOrder);
                }

                if (iteration % 5 == 0 && publishedHistory.Count > 0)
                {
                    SimulatedKafkaMessage replaySource = publishedHistory[random.Next(publishedHistory.Count)];
                    SimulatedKafkaMessage replay = replaySource with { ObservedAt = now.AddSeconds(random.Next(1, 4)) };
                    await EnqueueAsync(replay, "replay", stoppingToken).ConfigureAwait(false);
                }

                if (iteration % 7 == 0)
                {
                    string burstEntity = EntityIds[random.Next(EntityIds.Length)];
                    double burstAmount = Math.Round(random.NextDouble() * 10 + 2.0, 2);
                    DateTimeOffset burstTimestamp = now.AddMilliseconds(random.Next(-750, 750));
                    SimulatedKafkaMessage burst = new($"msg-burst-{iteration:000000}", burstEntity, burstAmount, burstTimestamp);
                    await EnqueueAsync(burst, "burst", stoppingToken).ConfigureAwait(false);
                    publishedHistory.Add(burst);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }

    private async Task EnqueueAsync(SimulatedKafkaMessage message, string scenario, CancellationToken cancellationToken)
    {
        string payload = QueueMessageSerializer.Serialize(message);
        await _queueClient.SendMessageAsync(BinaryData.FromString(payload), cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "[{Scenario}] Enqueued {MessageId} -> {EntityId} ({Amount:F2}) at {ObservedAt:o}",
            scenario,
            message.MessageId,
            message.EntityId,
            message.Amount,
            message.ObservedAt);
    }
}
