using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Provides helpers for configuring replication serialization metadata.
/// </summary>
public static class TaskQueueReplicationJsonSerialization
{
    /// <summary>
    /// Creates <see cref="JsonSerializerOptions"/> that include metadata for <see cref="TaskQueueReplicationEvent{T}"/>.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="template">Optional options to clone.</param>
    [RequiresUnreferencedCode("Uses DefaultJsonTypeInfoResolver to configure replication metadata.")]
    [RequiresDynamicCode("DefaultJsonTypeInfoResolver relies on runtime code generation.")]
    public static JsonSerializerOptions CreateOptions<T>(JsonSerializerOptions? template = null)
    {
        JsonSerializerOptions options = template is null ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions(template);
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(options.TypeInfoResolver, new TaskQueueReplicationJsonTypeInfoResolver<T>());
        return options;
    }

    /// <summary>
    /// Creates an <see cref="IJsonTypeInfoResolver"/> for replication events without cloning options.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    [RequiresUnreferencedCode("Uses DefaultJsonTypeInfoResolver to configure replication metadata.")]
    [RequiresDynamicCode("DefaultJsonTypeInfoResolver relies on runtime code generation.")]
    public static IJsonTypeInfoResolver CreateResolver<T>() => new TaskQueueReplicationJsonTypeInfoResolver<T>();

    [RequiresUnreferencedCode("Uses DefaultJsonTypeInfoResolver to configure replication metadata.")]
    [RequiresDynamicCode("DefaultJsonTypeInfoResolver relies on runtime code generation.")]
    private sealed class TaskQueueReplicationJsonTypeInfoResolver<T> : DefaultJsonTypeInfoResolver
    {
        public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(TaskQueueReplicationEvent<T>))
            {
                return base.GetTypeInfo(type, options);
            }

            return base.GetTypeInfo(type, options);
        }
    }
}
