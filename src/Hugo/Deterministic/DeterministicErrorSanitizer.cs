using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Hugo;

internal static class DeterministicErrorSanitizer
{
    /// <summary>
    /// Produces a sanitized <see cref="Error"/> graph that eliminates non-deterministic metadata payloads.
    /// </summary>
    /// <param name="error">The error to sanitize.</param>
    /// <returns>A sanitized error instance, or <c>null</c> when the input was <c>null</c>.</returns>
    public static Error? Sanitize(Error? error)
    {
        if (error is null)
        {
            return null;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Sanitize(error, visited);
    }

    /// <summary>
    /// Sanitizes an error metadata value so that persisted payloads remain deterministic and serializable.
    /// </summary>
    /// <param name="value">The value to sanitize.</param>
    /// <returns>The sanitized value, or <c>null</c> when the input was <c>null</c>.</returns>
    public static object? SanitizeMetadataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return SanitizeValue(value, visited);
    }

    /// <summary>
    /// Recursively sanitizes an error while preventing infinite recursion caused by cyclic references.
    /// </summary>
    /// <param name="error">The error to sanitize.</param>
    /// <param name="visited">The set tracking already visited instances.</param>
    /// <returns>The sanitized error instance.</returns>
    private static Error Sanitize(Error error, HashSet<object> visited)
    {
        if (!visited.Add(error))
        {
            return error;
        }

        var sanitizedMetadata = SanitizeMetadataDictionary(error.Metadata, visited, out var changed);
        var cause = error.Cause;

        if (!changed)
        {
            return error;
        }

        return Error.From(error.Message, error.Code, cause, sanitizedMetadata);
    }

    /// <summary>
    /// Sanitizes error metadata values to ensure they can be serialized deterministically.
    /// </summary>
    /// <param name="metadata">The metadata collection to sanitize.</param>
    /// <param name="visited">The set tracking already visited instances.</param>
    /// <param name="changed">Indicates whether the returned metadata differs from the original.</param>
    /// <returns>The sanitized metadata collection.</returns>
    private static IReadOnlyDictionary<string, object?> SanitizeMetadataDictionary(
        IReadOnlyDictionary<string, object?> metadata,
        HashSet<object> visited,
        out bool changed)
    {
        if (metadata.Count == 0)
        {
            changed = false;
            return metadata;
        }

        var builder = new Dictionary<string, object?>(metadata.Count, StringComparer.OrdinalIgnoreCase);
        changed = false;

        foreach (var kvp in metadata)
        {
            var sanitized = SanitizeValue(kvp.Value, visited);
            builder[kvp.Key] = sanitized;
            if (!changed && !MetadataValuesEqual(kvp.Value, sanitized))
            {
                changed = true;
            }
        }

        return changed ? builder : metadata;
    }

    /// <summary>
    /// Sanitizes a metadata value into a deterministic, serializable representation.
    /// </summary>
    /// <param name="value">The value to sanitize.</param>
    /// <param name="visited">The set tracking already visited instances.</param>
    /// <returns>The sanitized value.</returns>
    private static object? SanitizeValue(object? value, HashSet<object> visited)
    {
        if (value is null)
        {
            return null;
        }

        if (IsSupportedPrimitive(value))
        {
            return value;
        }

        if (value is JsonElement jsonElement)
        {
            return jsonElement;
        }

        if (value is Error error)
        {
            return Sanitize(error, visited);
        }

        if (value is CancellationToken token)
        {
            return new Dictionary<string, object?>(2, StringComparer.OrdinalIgnoreCase)
            {
                ["isCancellationRequested"] = token.IsCancellationRequested,
                ["canBeCanceled"] = token.CanBeCanceled
            };
        }

        if (value is Exception exception)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = exception.GetType().FullName ?? exception.GetType().Name,
                ["message"] = exception.Message,
                ["stackTrace"] = exception.StackTrace
            };
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            if (!AddVisited(readOnlyDictionary, visited))
            {
                return readOnlyDictionary;
            }

            var dict = new Dictionary<string, object?>(readOnlyDictionary.Count, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var kvp in readOnlyDictionary)
            {
                var sanitized = SanitizeValue(kvp.Value, visited);
                dict[kvp.Key] = sanitized;
                if (!changed && !MetadataValuesEqual(kvp.Value, sanitized))
                {
                    changed = true;
                }
            }

            return changed ? dict : readOnlyDictionary;
        }

        if (value is IDictionary dictionary)
        {
            if (!AddVisited(dictionary, visited))
            {
                return dictionary;
            }

            var result = new Dictionary<string, object?>(dictionary.Count, StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? string.Empty;
                result[key] = SanitizeValue(entry.Value, visited);
            }

            return result;
        }

        if (value is IEnumerable enumerable and not string)
        {
            if (!AddVisited(enumerable, visited))
            {
                return enumerable;
            }

            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(SanitizeValue(item, visited));
            }

            return list.ToArray();
        }

        return value.ToString();
    }

    /// <summary>
    /// Determines whether the supplied value is a primitive that can be used verbatim.
    /// </summary>
    /// <param name="value">The value to evaluate.</param>
    /// <returns><c>true</c> when the value is already deterministic; otherwise <c>false</c>.</returns>
    private static bool IsSupportedPrimitive(object value) =>
        value is string
        || value is bool
        || value is byte
        || value is sbyte
        || value is short
        || value is ushort
        || value is int
        || value is uint
        || value is long
        || value is ulong
        || value is float
        || value is double
        || value is decimal
        || value is DateTime
        || value is DateTimeOffset
        || value is Guid
        || value is TimeSpan;

    /// <summary>
    /// Compares metadata values while respecting reference equality.
    /// </summary>
    /// <param name="original">The original value.</param>
    /// <param name="sanitized">The sanitized value.</param>
    /// <returns><c>true</c> when the values are equivalent; otherwise <c>false</c>.</returns>
    private static bool MetadataValuesEqual(object? original, object? sanitized)
    {
        if (ReferenceEquals(original, sanitized))
        {
            return true;
        }

        return Equals(original, sanitized);
    }

    /// <summary>
    /// Tracks whether a reference type has already been processed to guard against cycles.
    /// </summary>
    /// <param name="candidate">The value to record.</param>
    /// <param name="visited">The set tracking already visited instances.</param>
    /// <returns><c>true</c> when the value should be processed; otherwise <c>false</c>.</returns>
    private static bool AddVisited(object candidate, HashSet<object> visited)
    {
        if (candidate.GetType().IsValueType)
        {
            return true;
        }

        return visited.Add(candidate);
    }

    /// <summary>
    /// Provides reference equality semantics for tracking visited instances.
    /// </summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        /// <inheritdoc />
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        /// <inheritdoc />
        public int GetHashCode([DisallowNull] object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
