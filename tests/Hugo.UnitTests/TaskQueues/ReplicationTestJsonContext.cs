using System.Text.Json;
using System.Text.Json.Serialization;

using Hugo.TaskQueues.Replication;

namespace Hugo.UnitTests.TaskQueues;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(TaskQueueReplicationEvent<int>))]
internal sealed partial class ReplicationTestJsonContext : JsonSerializerContext;
