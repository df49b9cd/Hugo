using System;
using System.Threading;
using System.Threading.Tasks;

using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

using Hugo;
using Hugo.DeterministicWorkerSample.Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hugo.DeterministicWorkerSample.CloudQueue;

/// <summary>
/// Dequeues messages from Azure Storage queues and hands them to the deterministic processor.
/// </summary>
internal sealed class QueueWorker(
    QueueClient queueClient,
    DeterministicPipelineProcessor processor,
    ILogger<QueueWorker> logger) : BackgroundService
{
    private readonly QueueClient _queueClient = queueClient ?? throw new ArgumentNullException(nameof(queueClient));
    private readonly DeterministicPipelineProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly ILogger<QueueWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _queueClient.CreateIfNotExistsAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Queue worker consuming from {QueueName}.", _queueClient.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Response<QueueMessage[]> response = await _queueClient.ReceiveMessagesAsync(
                        maxMessages: 16,
                        visibilityTimeout: TimeSpan.FromSeconds(30),
                        cancellationToken: stoppingToken)
                    .ConfigureAwait(false);

                QueueMessage[] messages = response.Value;

                if (messages.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (QueueMessage message in messages)
                {
                    if (!QueueMessageSerializer.TryDeserialize(message.MessageText, out SimulatedKafkaMessage? payload) || payload is null)
                    {
                        _logger.LogWarning("Discarded malformed queue message {MessageId}.", message.MessageId);
                        await _queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    Result<ProcessingOutcome> result = await _processor.ProcessAsync(payload, stoppingToken).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        await _queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        Error error = result.Error ?? Error.Unspecified();
                        _logger.LogError(
                            "Failed processing queue message {MessageId} for {EntityId}: {ErrorCode} - {ErrorMessage}",
                            payload.MessageId,
                            payload.EntityId,
                            error.Code ?? ErrorCodes.Unspecified,
                            error.Message);

                        // Let the message become visible again for retry.
                        await _queueClient.UpdateMessageAsync(
                            message.MessageId,
                            message.PopReceipt,
                            message.MessageText,
                            visibilityTimeout: TimeSpan.FromSeconds(15),
                            cancellationToken: stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (RequestFailedException ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Queue receive loop encountered a transient failure. Retrying shortly.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
