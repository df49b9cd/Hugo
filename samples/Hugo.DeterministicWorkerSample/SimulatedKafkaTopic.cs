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

    public ChannelReader<SimulatedKafkaMessage> Reader => _channel.Reader;

    public ValueTask PublishAsync(SimulatedKafkaMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        // Writers push messages into the unbounded channel; the worker consumes them sequentially.
        return _channel.Writer.WriteAsync(message, cancellationToken);
    }
}

sealed record class SimulatedKafkaMessage(string MessageId, string EntityId, double Amount, DateTimeOffset ObservedAt)
{
    // Helper to mint a unique message id when scripting additional publishes.
    public static SimulatedKafkaMessage Create(string entityId, double amount, DateTimeOffset observedAt) =>
        new($"msg-{Guid.NewGuid():N}", entityId, amount, observedAt);
}
