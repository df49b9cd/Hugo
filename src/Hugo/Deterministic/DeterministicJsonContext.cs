using System.Text.Json.Serialization;

namespace Hugo;

/// <summary>
/// Provides source-generated serialization metadata for deterministic persistence primitives.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(DeterministicEffectStore.EffectEnvelope))]
[JsonSerializable(typeof(VersionGate.VersionMarker))]
[JsonSerializable(typeof(Error))]
internal sealed partial class DeterministicJsonContext : JsonSerializerContext;
