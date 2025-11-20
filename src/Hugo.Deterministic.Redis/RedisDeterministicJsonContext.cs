using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hugo.Deterministic.Redis;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RedisDeterministicStateStore.DeterministicPayload))]
internal sealed partial class RedisDeterministicJsonContext : JsonSerializerContext;
