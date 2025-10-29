using System.Text.Json.Serialization;

namespace Hugo.DeterministicWorkerSample.Core;

/// <summary>
/// Provides source-generated metadata for deterministic pipeline models persisted by the sample.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
// EffectStore needs metadata for these types so replay can deserialize without reflection.
[JsonSerializable(typeof(PipelineEntity))]
[JsonSerializable(typeof(ProcessingOutcome))]
internal partial class DeterministicPipelineSerializerContext : JsonSerializerContext;
