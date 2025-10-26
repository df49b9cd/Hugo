using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hugo;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DeterministicEffectStore.EffectEnvelope))]
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(Error[]))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(KeyValuePair<string, object?>[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(object[]))]
internal partial class DeterministicJsonSerializerContext : JsonSerializerContext
{
}
