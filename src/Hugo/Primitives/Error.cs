using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Hugo;

/// <summary>
/// Represents a structured error with optional metadata, code, and cause.
/// </summary>
[JsonConverter(typeof(ErrorJsonConverter))]
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error is the canonical domain concept and renaming would break the public API.")]
public sealed class Error
{
    private const string DescriptorNameKey = "error.name";
    private const string DescriptorDescriptionKey = "error.description";
    private const string DescriptorCategoryKey = "error.category";

    private static readonly StringComparer MetadataComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly FrozenDictionary<string, object?> EmptyMetadata =
        FrozenDictionary.ToFrozenDictionary(Array.Empty<KeyValuePair<string, object?>>(), MetadataComparer);

    private readonly FrozenDictionary<string, object?> _metadata;

    private Error(string message, string? code, Exception? cause, FrozenDictionary<string, object?> metadata)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Code = code;
        Cause = cause;
        _metadata = AttachDescriptor(metadata ?? EmptyMetadata, code);
    }

    /// <summary>Gets the human-readable error message.</summary>
    public string Message { get; }

    /// <summary>Gets an optional error code that categorizes the error.</summary>
    public string? Code { get; }

    /// <summary>Gets the underlying exception that triggered the error, if any.</summary>
    public Exception? Cause { get; }

    /// <summary>Gets metadata associated with the error.</summary>
    public IReadOnlyDictionary<string, object?> Metadata => _metadata;

    /// <summary>Adds or replaces a metadata entry and returns a new <see cref="Error"/> instance.</summary>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">The metadata value.</param>
    /// <returns>A new error instance containing the updated metadata.</returns>
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

    /// <summary>Adds or replaces metadata entries from a contiguous span without additional allocations.</summary>
    /// <param name="metadata">The metadata entries to merge.</param>
    /// <returns>A new error instance containing the merged metadata.</returns>
    public Error WithMetadata(ReadOnlySpan<KeyValuePair<string, object?>> metadata)
    {
        if (metadata.IsEmpty)
        {
            return this;
        }

        var builder = CloneMetadata(_metadata, metadata.Length);
        for (int i = 0; i < metadata.Length; i++)
        {
            ref readonly var kvp = ref metadata[i];
            builder[kvp.Key] = kvp.Value;
        }

        return new Error(Message, Code, Cause, FreezeMetadata(builder));
    }

    /// <summary>Merges the supplied metadata entries into the error.</summary>
    /// <param name="metadata">The metadata entries to merge.</param>
    /// <returns>A new error instance containing the merged metadata.</returns>
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

    /// <summary>Creates a new error with the specified code.</summary>
    /// <param name="code">The error code to assign.</param>
    /// <returns>A new error instance containing the provided code.</returns>
    public Error WithCode(string? code) => new(Message, code, Cause, _metadata);

    /// <summary>Creates a new error with the specified cause.</summary>
    /// <param name="cause">The underlying exception that caused the error.</param>
    /// <returns>A new error instance containing the provided cause.</returns>
    public Error WithCause(Exception? cause) => new(Message, Code, cause, _metadata);

    /// <summary>Attempts to retrieve a metadata entry of the specified type.</summary>
    /// <typeparam name="T">The expected metadata type.</typeparam>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">When this method returns, contains the value if found and of the correct type.</param>
    /// <returns><see langword="true"/> when the metadata entry exists and matches <typeparamref name="T"/>; otherwise <see langword="false"/>.</returns>
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

    /// <summary>Returns a string representation of the error.</summary>
    /// <returns>A string that combines the code and message when available.</returns>
    public override string ToString() => Code is null ? Message : $"{Code}: {Message}";

    /// <summary>Creates an error from the supplied message, code, cause, and metadata.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="code">An optional error code.</param>
    /// <param name="cause">An optional exception that caused the error.</param>
    /// <param name="metadata">Optional metadata associated with the error.</param>
    /// <returns>A new error instance.</returns>
    public static Error From(string message, string? code = null, Exception? cause = null, IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(message, code, cause, FreezeMetadata(metadata));

    /// <summary>Creates an error using span-based metadata to minimize intermediary allocations.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="code">An optional error code.</param>
    /// <param name="cause">An optional exception that caused the error.</param>
    /// <param name="metadata">Optional metadata entries provided as a span.</param>
    /// <returns>A new error instance.</returns>
    public static Error From(string message, string? code, Exception? cause, ReadOnlySpan<KeyValuePair<string, object?>> metadata) =>
        new(message, code, cause, FreezeMetadata(metadata));

    /// <summary>Creates an error from an exception.</summary>
    /// <param name="exception">The exception to convert.</param>
    /// <param name="code">An optional error code.</param>
    /// <returns>A new error instance describing the exception.</returns>
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

    /// <summary>Creates a cancellation error that optionally captures the owning token.</summary>
    /// <param name="message">An optional message describing the cancellation.</param>
    /// <param name="token">The token that triggered the cancellation.</param>
    /// <returns>A new cancellation error.</returns>
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

    /// <summary>Creates a timeout error.</summary>
    /// <param name="duration">The duration associated with the timeout.</param>
    /// <param name="message">An optional message describing the timeout.</param>
    /// <returns>A new timeout error.</returns>
    public static Error Timeout(TimeSpan? duration = null, string? message = null)
    {
        var builder = duration.HasValue
            ? new Dictionary<string, object?>(1, MetadataComparer) { ["duration"] = duration.Value }
            : null;

        return new Error(message ?? "The operation timed out.", ErrorCodes.Timeout, null, FreezeMetadata(builder));
    }

    /// <summary>Creates a generic error without a specific code.</summary>
    /// <param name="message">An optional message describing the error.</param>
    /// <returns>A new error instance.</returns>
    public static Error Unspecified(string? message = null) => new(message ?? "An unspecified error occurred.", ErrorCodes.Unspecified, null, EmptyMetadata);

    /// <summary>Creates an aggregate error.</summary>
    /// <param name="message">The message describing the aggregate failure.</param>
    /// <param name="errors">The errors to combine.</param>
    /// <returns>A new aggregate error.</returns>
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

    /// <summary>Implicit conversion from a message to an error.</summary>
    /// <param name="message">The message to convert.</param>
    /// <returns>An error representing the message.</returns>
    public static implicit operator Error?(string? message) => message is null ? null : From(message);

    /// <summary>Implicit conversion from an exception to an error.</summary>
    /// <param name="exception">The exception to convert.</param>
    /// <returns>An error representing the exception.</returns>
    public static implicit operator Error?(Exception? exception) => exception is null ? null : FromException(exception);

    private static FrozenDictionary<string, object?> AttachDescriptor(FrozenDictionary<string, object?> metadata, string? code)
    {
        if (code is null || !ErrorCodes.TryGetDescriptor(code, out var descriptor))
        {
            return metadata;
        }

        bool hasName = metadata.ContainsKey(DescriptorNameKey);
        bool hasDescription = metadata.ContainsKey(DescriptorDescriptionKey);
        bool hasCategory = metadata.ContainsKey(DescriptorCategoryKey);

        if (hasName && hasDescription && hasCategory)
        {
            return metadata;
        }

        var builder = CloneMetadata(metadata, 3);

        if (!hasName)
        {
            builder.TryAdd(DescriptorNameKey, descriptor.Name);
        }

        if (!hasDescription)
        {
            builder.TryAdd(DescriptorDescriptionKey, descriptor.Description);
        }

        if (!hasCategory)
        {
            builder.TryAdd(DescriptorCategoryKey, descriptor.Category);
        }

        return FreezeMetadata(builder);
    }

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

    private static FrozenDictionary<string, object?> FreezeMetadata(ReadOnlySpan<KeyValuePair<string, object?>> metadata)
    {
        if (metadata.IsEmpty)
        {
            return EmptyMetadata;
        }

        var builder = new Dictionary<string, object?>(metadata.Length, MetadataComparer);
        for (int i = 0; i < metadata.Length; i++)
        {
            ref readonly var kvp = ref metadata[i];
            builder[kvp.Key] = kvp.Value;
        }

        return builder.ToFrozenDictionary(MetadataComparer);
    }

    private static FrozenDictionary<string, object?> FreezeMetadata(Dictionary<string, object?>? builder) => builder is null || builder.Count == 0 ? EmptyMetadata : builder.ToFrozenDictionary(MetadataComparer);

    public Error? ToError() => throw new NotImplementedException();
}
