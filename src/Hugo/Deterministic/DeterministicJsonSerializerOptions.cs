using System.Text.Json;

namespace Hugo;

/// <summary>
/// Provides a single place to clone and initialize <see cref="JsonSerializerOptions"/> used for deterministic persistence.
/// </summary>
internal static class DeterministicJsonSerializerOptions
{
    public static JsonSerializerOptions Create(JsonSerializerOptions? template = null) =>
        template is null ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions(template);
}
