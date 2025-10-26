using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Hugo;

internal static class DeterministicErrorSanitizer
{
    public static Error? Sanitize(Error? error)
    {
        if (error is null)
        {
            return null;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Sanitize(error, visited);
    }

    public static object? SanitizeMetadataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return SanitizeValue(value, visited);
    }

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

    private static bool MetadataValuesEqual(object? original, object? sanitized)
    {
        if (ReferenceEquals(original, sanitized))
        {
            return true;
        }

        return Equals(original, sanitized);
    }

    private static bool AddVisited(object candidate, HashSet<object> visited)
    {
        if (candidate.GetType().IsValueType)
        {
            return true;
        }

        return visited.Add(candidate);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode([DisallowNull] object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
