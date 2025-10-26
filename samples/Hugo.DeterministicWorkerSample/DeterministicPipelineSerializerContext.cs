using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PipelineEntity))]
[JsonSerializable(typeof(ProcessingOutcome))]
internal partial class DeterministicPipelineSerializerContext : JsonSerializerContext
{
}
