using System.Threading.Channels;

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

/// <summary>
/// Represents a simulated Kafka message produced by the sample scenario.
/// </summary>
sealed record class SimulatedKafkaMessage(string MessageId, string EntityId, double Amount, DateTimeOffset ObservedAt)
{
    /// <summary>
    /// Creates a new message with a generated identifier for ad-hoc publishing.
    /// </summary>
    /// <param name="entityId">The entity associated with the message.</param>
    /// <param name="amount">The amount to aggregate.</param>
    /// <param name="observedAt">The timestamp observed by the producer.</param>
    /// <returns>A simulated Kafka message with a unique identifier.</returns>
    public static SimulatedKafkaMessage Create(string entityId, double amount, DateTimeOffset observedAt) =>
        new($"msg-{Guid.NewGuid():N}", entityId, amount, observedAt);
}
