using System.Text.Json;

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
public sealed class DeterministicEffectStore(IDeterministicStateStore store, TimeProvider? timeProvider = null, JsonSerializerOptions? serializerOptions = null)
{
    private const string RecordKind = "hugo.effect";

    private static readonly JsonSerializerOptions DefaultSerializerOptions = DeterministicJsonSerializerOptions.Create();

    private readonly IDeterministicStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly JsonSerializerOptions _serializerOptions = serializerOptions is null
        ? DefaultSerializerOptions
        : DeterministicJsonSerializerOptions.Create(serializerOptions);

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

        if (_store.TryGet(effectId, out var record))
        {
            return Replay<T>(effectId, record);
        }

        Result<T> outcome;
        try
        {
            outcome = await effect(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = Result.Fail<T>(Error.FromException(ex));
        }

        await PersistAsync(effectId, outcome).ConfigureAwait(false);
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

    private Task PersistAsync<T>(string effectId, Result<T> outcome)
    {
        var now = _timeProvider.GetUtcNow();
        byte[]? valuePayload = null;

        if (outcome.IsSuccess)
        {
            valuePayload = JsonSerializer.SerializeToUtf8Bytes(outcome.Value, _serializerOptions);
        }

        var sanitizedError = DeterministicErrorSanitizer.Sanitize(outcome.Error);

        var envelope = new EffectEnvelope(
            outcome.IsSuccess,
            typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name,
            valuePayload,
            sanitizedError,
            now);

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, _serializerOptions);
        var record = new DeterministicRecord(RecordKind, version: 0, payload, now);
        _store.Set(effectId, record);
        return Task.CompletedTask;
    }

    private EffectEnvelope DeserializeEnvelope(ReadOnlySpan<byte> payload)
    {
        var envelope = JsonSerializer.Deserialize<EffectEnvelope>(payload, _serializerOptions);
        if (envelope is null)
        {
            throw new InvalidOperationException("Unable to deserialize deterministic effect envelope.");
        }

        return envelope;
    }

    private T DeserializeValue<T>(byte[]? payload)
    {
        if (payload is null)
        {
            return default!;
        }

        return JsonSerializer.Deserialize<T>(payload, _serializerOptions)!;
    }

    private sealed record EffectEnvelope(bool IsSuccess, string TypeName, byte[]? SerializedValue, Error? Error, DateTimeOffset RecordedAt);
}
