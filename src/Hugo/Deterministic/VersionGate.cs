using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Hugo;

/// <summary>
/// Coordinates deterministic workflow branching by recording version markers that persist across replays.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="VersionGate"/> class.
/// </remarks>
/// <param name="store">Backing store used to persist version markers.</param>
/// <param name="timeProvider">Optional <see cref="TimeProvider"/> for deterministic timestamps.</param>
/// <param name="serializerOptions">Serializer options used for payload persistence.</param>
public sealed class VersionGate(IDeterministicStateStore store, TimeProvider? timeProvider = null, JsonSerializerOptions? serializerOptions = null)
{
    private const string RecordKind = "hugo.version";

    private static readonly JsonSerializerOptions DefaultSerializerOptions = DeterministicJsonSerializerOptions.Create();

    private readonly IDeterministicStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly JsonSerializerOptions _serializerOptions = serializerOptions is null
        ? DefaultSerializerOptions
        : DeterministicJsonSerializerOptions.Create(serializerOptions);

    /// <summary>
    /// Sentinel value that mirrors Temporal's <c>DefaultVersion</c> and indicates the absence of a recorded version.
    /// </summary>
    public const int DefaultVersion = -1;

    /// <summary>
    /// Records or retrieves a version marker for the specified change identifier.
    /// </summary>
    /// <param name="changeId">Unique change identifier.</param>
    /// <param name="minSupportedVersion">Minimum supported version for the change.</param>
    /// <param name="maxSupportedVersion">Maximum supported version for the change.</param>
    /// <param name="initialVersionProvider">Optional factory used when no marker is present. Defaults to <paramref name="maxSupportedVersion"/>.</param>
    /// <returns>A <see cref="Result{T}"/> describing the resolved version decision.</returns>
    public Result<VersionDecision> Require(
        string changeId,
        int minSupportedVersion,
        int maxSupportedVersion,
        Func<VersionGateContext, int>? initialVersionProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeId);

        if (minSupportedVersion > maxSupportedVersion)
        {
            return Result.Fail<VersionDecision>(Error.From(
                $"Minimum supported version {minSupportedVersion} exceeds maximum {maxSupportedVersion}.",
                ErrorCodes.Validation).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["changeId"] = changeId,
                    ["minVersion"] = minSupportedVersion,
                    ["maxVersion"] = maxSupportedVersion
                }));
        }

        if (_store.TryGet(changeId, out var record))
        {
            return ResolveExistingRecord(changeId, record, minSupportedVersion, maxSupportedVersion);
        }

        var context = new VersionGateContext(changeId, minSupportedVersion, maxSupportedVersion);
        int decidedVersion;
        try
        {
            decidedVersion = initialVersionProvider?.Invoke(context) ?? maxSupportedVersion;
        }
        catch (Exception ex)
        {
            return Result.Fail<VersionDecision>(Error.FromException(ex).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["changeId"] = changeId
            }));
        }

        if (decidedVersion < minSupportedVersion || decidedVersion > maxSupportedVersion)
        {
            return Result.Fail<VersionDecision>(Error.From(
                $"Initial version {decidedVersion} is outside the supported range {minSupportedVersion}..{maxSupportedVersion}.",
                ErrorCodes.VersionConflict).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["changeId"] = changeId,
                    ["initialVersion"] = decidedVersion,
                    ["minVersion"] = minSupportedVersion,
                    ["maxVersion"] = maxSupportedVersion
                }));
        }

        var now = _timeProvider.GetUtcNow();
        var payload = SerializeVersion(decidedVersion);
        var newRecord = new DeterministicRecord(RecordKind, decidedVersion, payload, now);

        if (_store.TryAdd(changeId, newRecord))
        {
            return Result.Ok(new VersionDecision(decidedVersion, true, now));
        }

        if (_store.TryGet(changeId, out var concurrentRecord))
        {
            return ResolveExistingRecord(changeId, concurrentRecord, minSupportedVersion, maxSupportedVersion);
        }

        return Result.Fail<VersionDecision>(Error.From(
            "Concurrent version gate update prevented recording a decision.",
            ErrorCodes.VersionConflict).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["changeId"] = changeId,
                ["minVersion"] = minSupportedVersion,
                ["maxVersion"] = maxSupportedVersion
            }));
    }

    /// <summary>
    /// Serializes the supplied version marker for persistence.
    /// </summary>
    /// <param name="version">The version to serialize.</param>
    /// <returns>The serialized payload.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Version markers serialize a known record type.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Version markers serialize a known record type.")]
    private byte[] SerializeVersion(int version) => JsonSerializer.SerializeToUtf8Bytes(new VersionMarker(version), _serializerOptions);

    /// <summary>
    /// Deserializes a persisted version marker payload.
    /// </summary>
    /// <param name="payload">The serialized payload.</param>
    /// <returns>The materialized version value.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Version markers serialize a known record type.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Version markers serialize a known record type.")]
    private int DeserializeVersion(ReadOnlySpan<byte> payload)
    {
        var marker = JsonSerializer.Deserialize<VersionMarker>(payload, _serializerOptions);
        if (marker is null)
        {
            throw new InvalidOperationException("Unable to deserialize persisted version marker.");
        }

        return marker.Version;
    }

    private Result<VersionDecision> ResolveExistingRecord(string changeId, DeterministicRecord record, int minSupportedVersion, int maxSupportedVersion)
    {
        if (!string.Equals(record.Kind, RecordKind, StringComparison.Ordinal))
        {
            return Result.Fail<VersionDecision>(Error.From(
                $"Deterministic record kind '{record.Kind}' does not match expected '{RecordKind}'.",
                ErrorCodes.DeterministicReplay).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["changeId"] = changeId,
                    ["kind"] = record.Kind
                }));
        }

        var persistedVersion = DeserializeVersion(record.Payload.Span);
        if (persistedVersion < minSupportedVersion || persistedVersion > maxSupportedVersion)
        {
            return Result.Fail<VersionDecision>(Error.From(
                $"Recorded version {persistedVersion} falls outside supported range {minSupportedVersion}..{maxSupportedVersion}.",
                ErrorCodes.VersionConflict).WithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["changeId"] = changeId,
                    ["persistedVersion"] = persistedVersion,
                    ["minVersion"] = minSupportedVersion,
                    ["maxVersion"] = maxSupportedVersion
                }));
        }

        return Result.Ok(new VersionDecision(persistedVersion, false, record.RecordedAt));
    }

    internal sealed record VersionMarker(int Version);
}

/// <summary>
/// Represents a deterministic version decision.
/// </summary>
/// <param name="Version">Resolved version number.</param>
/// <param name="IsNew">Indicates whether the decision recorded a new marker.</param>
/// <param name="RecordedAt">Timestamp associated with the decision.</param>
public sealed record VersionDecision(int Version, bool IsNew, DateTimeOffset RecordedAt);

/// <summary>
/// Provides context information to <see cref="VersionGate"/> callbacks.
/// </summary>
/// <param name="ChangeId">Unique change identifier.</param>
/// <param name="MinVersion">Minimum supported version.</param>
/// <param name="MaxVersion">Maximum supported version.</param>
public readonly record struct VersionGateContext(string ChangeId, int MinVersion, int MaxVersion);
