using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Hugo;

/// <summary>
/// Records side-effect results so that replayed workflow executions observe deterministic outcomes.
/// Error metadata is sanitized to ensure persisted payloads remain stable and JSON-serializable.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DeterministicEffectStore"/> class.
/// </remarks>
/// <param name="store">Backing store used to persist side-effect outcomes.</param>
/// <param name="timeProvider">Optional time provider used for timestamping.</param>
/// <param name="serializerOptions">Serializer options used when materializing values.</param>
/// <param name="serializerContext">Serialization metadata used for deterministic envelopes.</param>
public sealed class DeterministicEffectStore
{
    private const string RecordKind = "hugo.effect";

    private static readonly JsonSerializerOptions DefaultSerializerOptions = DeterministicJsonSerializerOptions.Create();
    private static readonly JsonSerializerContext DefaultSerializerContext = DeterministicJsonSerialization.DefaultContext;

    private readonly IDeterministicStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerContext _serializerContext;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly JsonTypeInfo<EffectEnvelope> _effectEnvelopeTypeInfo;

    /// <summary>
    /// Gets the <see cref="JsonSerializerContext"/> used for deterministic helper types.
    /// </summary>
    public JsonSerializerContext SerializerContext => _serializerContext;

    /// <summary>
    /// Gets the shared deterministic serializer context that includes Hugo's helper metadata.
    /// </summary>
    public static JsonSerializerContext DefaultDeterministicContext => DefaultSerializerContext;

    /// <summary>
    /// Creates an effect store that uses <see cref="DefaultDeterministicContext"/>.
    /// </summary>
    /// <param name="store">The deterministic state store.</param>
    /// <param name="timeProvider">Optional time provider.</param>
    /// <param name="serializerOptions">Serializer options used for effect values.</param>
    /// <returns>A configured <see cref="DeterministicEffectStore"/>.</returns>
    public static DeterministicEffectStore CreateDefault(
        IDeterministicStateStore store,
        TimeProvider? timeProvider = null,
        JsonSerializerOptions? serializerOptions = null) =>
        new(
            store,
            timeProvider,
            serializerOptions,
            serializerOptions is null ? DefaultSerializerContext : DeterministicJsonSerialization.CreateContext(serializerOptions));

    /// <summary>
    /// Initializes a new instance of the <see cref="DeterministicEffectStore"/> class.
    /// </summary>
    public DeterministicEffectStore(
        IDeterministicStateStore store,
        TimeProvider? timeProvider = null,
        JsonSerializerOptions? serializerOptions = null,
        JsonSerializerContext? serializerContext = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serializerContext = serializerContext
            ?? (serializerOptions is null ? DefaultSerializerContext : DeterministicJsonSerialization.CreateContext(serializerOptions));
        _serializerOptions = serializerOptions is null
            ? (_serializerContext.Options ?? DefaultSerializerOptions)
            : DeterministicJsonSerializerOptions.Create(serializerOptions);
        _effectEnvelopeTypeInfo = RequireTypeInfo<EffectEnvelope>(_serializerContext);
    }

    /// <summary>
    /// Executes the provided side-effect once and replays the persisted outcome on subsequent invocations.
    /// </summary>
    /// <typeparam name="T">The side-effect result type.</typeparam>
    /// <param name="effectId">Unique side-effect identifier.</param>
    /// <param name="effect">Factory that produces the side-effect result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A deterministic <see cref="Result{T}"/>.</returns>
    public async Task<Result<T>> CaptureAsync<T>(
        string effectId,
        Func<CancellationToken, Task<Result<T>>> effect,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        ArgumentNullException.ThrowIfNull(effect);

        using Activity? activity = GoDiagnostics.StartDeterministicActivity("effect.capture", effectId: effectId);

        if (_store.TryGet(effectId, out var record))
        {
            var replay = Replay<T>(effectId, record);
            GoDiagnostics.CompleteDeterministicActivity(activity, replay.Error);
            return replay;
        }

        Result<Result<T>> invocation = await Result
            .TryAsync(
                async ct => await effect(ct).ConfigureAwait(false),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (invocation.IsFailure && invocation.Error?.Cause is OperationCanceledException oce)
        {
            throw oce;
        }

        Result<T> outcome = invocation.Then(static inner => inner);

        await PersistAsync(effectId, outcome).ConfigureAwait(false);
        GoDiagnostics.CompleteDeterministicActivity(activity, outcome.Error);
        return outcome;
    }

    /// <summary>
    /// Executes the provided side-effect once and replays the persisted outcome on subsequent invocations.
    /// </summary>
    /// <typeparam name="T">The side-effect result type.</typeparam>
    /// <param name="effectId">Unique side-effect identifier.</param>
    /// <param name="effect">Factory that produces the side-effect result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A deterministic <see cref="Result{T}"/>.</returns>
    public Task<Result<T>> CaptureAsync<T>(
        string effectId,
        Func<Task<Result<T>>> effect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return CaptureAsync(effectId, _ => effect(), cancellationToken);
    }

    /// <summary>
    /// Executes the provided side-effect once and replays the persisted outcome on subsequent invocations.
    /// </summary>
    /// <typeparam name="T">The side-effect result type.</typeparam>
    /// <param name="effectId">Unique side-effect identifier.</param>
    /// <param name="effect">Factory that produces the side-effect result.</param>
    /// <returns>A deterministic <see cref="Result{T}"/>.</returns>
    public Task<Result<T>> CaptureAsync<T>(string effectId, Func<Result<T>> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return CaptureAsync(effectId, _ => Task.FromResult(effect()));
    }

    /// <summary>
    /// Replays a previously captured deterministic effect.
    /// </summary>
    /// <typeparam name="T">The expected result type.</typeparam>
    /// <param name="effectId">The effect identifier.</param>
    /// <param name="record">The persisted record.</param>
    /// <returns>The replayed result or a failure when the stored payload is incompatible.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Effect payloads serialize arbitrary user types.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Effect payloads serialize arbitrary user types.")]
    private Result<T> Replay<T>(string effectId, DeterministicRecord record)
    {
        if (!string.Equals(record.Kind, RecordKind, StringComparison.Ordinal))
        {
            return Result.Fail<T>(Error.From(
                $"Deterministic record kind '{record.Kind}' does not match expected '{RecordKind}'.",
                ErrorCodes.DeterministicReplay).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["effectId"] = effectId,
                    ["kind"] = record.Kind
                }));
        }

        var envelope = DeserializeEnvelope(record.Payload.Span);
        if (!string.Equals(envelope.TypeName, typeof(T).AssemblyQualifiedName, StringComparison.Ordinal))
        {
            return Result.Fail<T>(Error.From(
                $"Stored effect type '{envelope.TypeName}' does not match requested type '{typeof(T).AssemblyQualifiedName}'.",
                ErrorCodes.DeterministicReplay).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["effectId"] = effectId,
                    ["storedType"] = envelope.TypeName,
                    ["requestedType"] = typeof(T).AssemblyQualifiedName
                }));
        }

        if (envelope.IsSuccess)
        {
            var value = DeserializeValue<T>(envelope.SerializedValue);
            return Result.Ok(value);
        }

        return Result.Fail<T>(envelope.Error ?? Error.Unspecified());
    }

    /// <summary>
    /// Persists the supplied effect outcome in the deterministic state store.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="effectId">The effect identifier.</param>
    /// <param name="outcome">The outcome to persist.</param>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Effect payloads serialize arbitrary user types.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Effect payloads serialize arbitrary user types.")]
    private Task PersistAsync<T>(string effectId, Result<T> outcome)
    {
        var now = _timeProvider.GetUtcNow();
        byte[]? valuePayload = null;

        if (outcome.IsSuccess)
        {
            if (TryGetTypeInfo<T>() is { } typeInfo)
            {
                valuePayload = JsonSerializer.SerializeToUtf8Bytes(outcome.Value, typeInfo);
            }
            else
            {
                valuePayload = JsonSerializer.SerializeToUtf8Bytes(outcome.Value, _serializerOptions);
            }
        }

        var sanitizedError = DeterministicErrorSanitizer.Sanitize(outcome.Error);

        var envelope = new EffectEnvelope(
            outcome.IsSuccess,
            typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name,
            valuePayload,
            sanitizedError,
            now);

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, _effectEnvelopeTypeInfo);
        var record = new DeterministicRecord(RecordKind, version: 0, payload, now);
        _store.Set(effectId, record);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Materializes a deterministic effect envelope from serialized payload.
    /// </summary>
    /// <param name="payload">The serialized envelope.</param>
    /// <returns>The deserialized envelope.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Effect payloads serialize arbitrary user types.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Effect payloads serialize arbitrary user types.")]
    private EffectEnvelope DeserializeEnvelope(ReadOnlySpan<byte> payload)
    {
        var envelope = JsonSerializer.Deserialize(payload, _effectEnvelopeTypeInfo);
        if (envelope is null)
        {
            throw new InvalidOperationException("Unable to deserialize deterministic effect envelope.");
        }

        return envelope;
    }

    /// <summary>
    /// Deserializes a persisted effect value.
    /// </summary>
    /// <typeparam name="T">The expected result type.</typeparam>
    /// <param name="payload">The serialized value.</param>
    /// <returns>The materialized value, or the default when the payload is <c>null</c>.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Effect payloads serialize arbitrary user types.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Effect payloads serialize arbitrary user types.")]
    private T DeserializeValue<T>(byte[]? payload)
    {
        if (payload is null)
        {
            return default!;
        }

        if (TryGetTypeInfo<T>() is { } typeInfo)
        {
            return JsonSerializer.Deserialize(payload, typeInfo)!;
        }

        return JsonSerializer.Deserialize<T>(payload, _serializerOptions)!;
    }

    internal sealed record EffectEnvelope(bool IsSuccess, string TypeName, byte[]? SerializedValue, Error? Error, DateTimeOffset RecordedAt);

    private JsonTypeInfo<T>? TryGetTypeInfo<T>()
    {
        if (_serializerContext.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> typeInfo)
        {
            return typeInfo;
        }

        return null;
    }

    private static JsonTypeInfo<T> RequireTypeInfo<T>(JsonSerializerContext context)
    {
        if (context.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> typeInfo)
        {
            return typeInfo;
        }

        throw new InvalidOperationException(
            $"The provided JsonSerializerContext '{context.GetType().Name}' does not expose metadata for '{typeof(T)}'. " +
            $"Use {nameof(DeterministicJsonSerialization)}.{nameof(DeterministicJsonSerialization.DefaultContext)} or pass a context that includes deterministic helper types.");
    }
}
