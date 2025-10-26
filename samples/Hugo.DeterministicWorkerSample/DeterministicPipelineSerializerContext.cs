using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
// EffectStore needs metadata for these types so replay can deserialize without reflection.
[JsonSerializable(typeof(PipelineEntity))]
[JsonSerializable(typeof(ProcessingOutcome))]
internal partial class DeterministicPipelineSerializerContext : JsonSerializerContext
{
}
