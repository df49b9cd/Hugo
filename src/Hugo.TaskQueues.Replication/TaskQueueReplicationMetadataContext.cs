using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hugo.TaskQueues.Replication;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TaskQueueReplicationEventKind))]
[JsonSerializable(typeof(TaskQueueOwnershipToken))]
[JsonSerializable(typeof(TaskQueueLifecycleEventMetadata))]
internal sealed partial class TaskQueueReplicationMetadataContext : JsonSerializerContext
{
    public static TaskQueueReplicationMetadataContext Default { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
