using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hugo;

/// <summary>
/// Provides a single place to clone and initialize <see cref="JsonSerializerOptions"/> used for deterministic persistence.
/// </summary>
internal static class DeterministicJsonSerializerOptions
{
    public static JsonSerializerOptions Create(JsonSerializerOptions? template = null)
    {
        var options = template is null ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions(template);

        if (!options.TypeInfoResolverChain.Contains(DeterministicJsonSerializerContext.Default))
        {
            options.TypeInfoResolverChain.Add(DeterministicJsonSerializerContext.Default);
        }

        return options;
    }
}
