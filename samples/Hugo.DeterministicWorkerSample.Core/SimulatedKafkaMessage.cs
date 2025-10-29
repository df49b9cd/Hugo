namespace Hugo.DeterministicWorkerSample.Core;

/// <summary>
/// Represents a simulated Kafka message produced by the sample scenario.
/// </summary>
public sealed record class SimulatedKafkaMessage(string MessageId, string EntityId, double Amount, DateTimeOffset ObservedAt)
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
