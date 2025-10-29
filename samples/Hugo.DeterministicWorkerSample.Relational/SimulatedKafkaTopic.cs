using System.Threading.Channels;

using Hugo.DeterministicWorkerSample.Core;

/// <summary>
/// Emulates a single-partition Kafka topic the sample can enqueue into without external dependencies.
/// </summary>
sealed class SimulatedKafkaTopic
{
    private readonly Channel<SimulatedKafkaMessage> _channel = Channel.CreateUnbounded<SimulatedKafkaMessage>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
    });

    /// <summary>
    /// Gets the channel reader that streams messages to consumers.
    /// </summary>
    public ChannelReader<SimulatedKafkaMessage> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues a message into the simulated topic.
    /// </summary>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">Token used to cancel the publish operation.</param>
    /// <returns>A task that completes when the message is enqueued.</returns>
    public ValueTask PublishAsync(SimulatedKafkaMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Writers push messages into the unbounded channel; the worker consumes them sequentially.
        return _channel.Writer.WriteAsync(message, cancellationToken);
    }
}
