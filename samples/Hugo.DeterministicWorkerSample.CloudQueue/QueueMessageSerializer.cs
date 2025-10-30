using System.Text.Json;

using Hugo.DeterministicWorkerSample.Core;

namespace Hugo.DeterministicWorkerSample.CloudQueue;

/// <summary>
/// Serializes <see cref="SimulatedKafkaMessage"/> instances for transport via Azure Queue storage.
/// </summary>
internal static class QueueMessageSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(SimulatedKafkaMessage message) => JsonSerializer.Serialize(message, SerializerOptions);

    public static bool TryDeserialize(string payload, out SimulatedKafkaMessage? message)
    {
        try
        {
            message = JsonSerializer.Deserialize<SimulatedKafkaMessage>(payload, SerializerOptions);
            return message is not null;
        }
        catch
        {
            message = null;
            return false;
        }
    }
}
