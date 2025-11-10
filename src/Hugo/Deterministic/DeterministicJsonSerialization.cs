using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hugo;

/// <summary>
/// Provides access to the source-generated deterministic serialization metadata.
/// </summary>
public static class DeterministicJsonSerialization
{
    /// <summary>
    /// Gets a shared <see cref="JsonSerializerContext"/> instance configured with deterministic defaults.
    /// </summary>
    public static JsonSerializerContext DefaultContext { get; } = new DeterministicJsonContext(DeterministicJsonSerializerOptions.Create());

    /// <summary>
    /// Creates a new deterministic <see cref="JsonSerializerContext"/> based on the supplied options.
    /// </summary>
    /// <param name="options">Serializer options used when generating metadata.</param>
    /// <returns>A deterministic serialization context.</returns>
    public static JsonSerializerContext CreateContext(JsonSerializerOptions? options = null) =>
        new DeterministicJsonContext(DeterministicJsonSerializerOptions.Create(options));
}
