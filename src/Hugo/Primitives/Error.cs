using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;

namespace Hugo;

/// <summary>
/// Represents a structured error with optional metadata, code, and cause.
/// </summary>
[JsonConverter(typeof(ErrorJsonConverter))]
public sealed class Error
{
    private static readonly StringComparer MetadataComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly FrozenDictionary<string, object?> EmptyMetadata =
        FrozenDictionary.ToFrozenDictionary(Array.Empty<KeyValuePair<string, object?>>(), MetadataComparer);

    private readonly FrozenDictionary<string, object?> _metadata;

    private Error(string message, string? code, Exception? cause, FrozenDictionary<string, object?> metadata)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Code = code;
        Cause = cause;
        _metadata = metadata ?? EmptyMetadata;
    }

    public string Message { get; }

    public string? Code { get; }

    public Exception? Cause { get; }

    public IReadOnlyDictionary<string, object?> Metadata => _metadata;

    public Error WithMetadata(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Metadata key must be provided.", nameof(key));
        }

        var builder = CloneMetadata(_metadata, 1);
        builder[key] = value;

        return new Error(Message, Code, Cause, FreezeMetadata(builder));
    }

    public Error WithMetadata(IEnumerable<KeyValuePair<string, object?>> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var builder = CloneMetadata(_metadata);
        foreach (var kvp in metadata)
        {
            builder[kvp.Key] = kvp.Value;
        }

        return new Error(Message, Code, Cause, FreezeMetadata(builder));
    }

    public Error WithCode(string? code) => new(Message, code, Cause, _metadata);

    public Error WithCause(Exception? cause) => new(Message, Code, cause, _metadata);

    public bool TryGetMetadata<T>(string key, out T? value)
    {
        if (_metadata.TryGetValue(key, out var obj) && obj is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public override string ToString() => Code is null ? Message : $"{Code}: {Message}";

    public static Error From(string message, string? code = null, Exception? cause = null, IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(message, code, cause, FreezeMetadata(metadata));

    public static Error FromException(Exception exception, string? code = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new Dictionary<string, object?>(2, MetadataComparer)
        {
            ["exceptionType"] = exception.GetType().FullName,
            ["stackTrace"] = exception.StackTrace
        };

        return new Error(exception.Message, code ?? ErrorCodes.Exception, exception, FreezeMetadata(builder));
    }

    public static Error Canceled(string? message = null, CancellationToken? token = null)
    {
        var builder = token is { } t
            ? new Dictionary<string, object?>(1, MetadataComparer) { ["cancellationToken"] = t }
            : null;

        return new Error(
            message ?? "Operation was canceled.",
            ErrorCodes.Canceled,
            token is { } tk ? new OperationCanceledException(tk) : null,
            FreezeMetadata(builder));
    }

    public static Error Timeout(TimeSpan? duration = null, string? message = null)
    {
        var builder = duration.HasValue
            ? new Dictionary<string, object?>(1, MetadataComparer) { ["duration"] = duration.Value }
            : null;

        return new Error(message ?? "The operation timed out.", ErrorCodes.Timeout, null, FreezeMetadata(builder));
    }

    public static Error Unspecified(string? message = null) => new(message ?? "An unspecified error occurred.", ErrorCodes.Unspecified, null, EmptyMetadata);

    public static Error Aggregate(string message, params Error[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Length == 0)
        {
            throw new ArgumentException("At least one error must be provided.", nameof(errors));
        }

        var builder = new Dictionary<string, object?>(1, MetadataComparer)
        {
            ["errors"] = errors
        };

        return new Error(message, ErrorCodes.Aggregate, null, FreezeMetadata(builder));
    }

    public static implicit operator Error?(string? message) => message is null ? null : From(message);

    public static implicit operator Error?(Exception? exception) => exception is null ? null : FromException(exception);

    private static Dictionary<string, object?> CloneMetadata(IReadOnlyDictionary<string, object?> source, int additionalCapacity = 0)
    {
        var builder = new Dictionary<string, object?>(source.Count + additionalCapacity, MetadataComparer);
        foreach (var kvp in source)
        {
            builder[kvp.Key] = kvp.Value;
        }

        return builder;
    }

    private static FrozenDictionary<string, object?> FreezeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return EmptyMetadata;
        }

        return metadata is FrozenDictionary<string, object?> frozen && MetadataComparer.Equals(frozen.Comparer)
            ? frozen
            : FreezeMetadata(CloneMetadata(metadata));
    }

    private static FrozenDictionary<string, object?> FreezeMetadata(Dictionary<string, object?>? builder) => builder is null || builder.Count == 0 ? EmptyMetadata : builder.ToFrozenDictionary(MetadataComparer);
}
