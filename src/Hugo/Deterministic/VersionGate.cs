using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
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
public sealed class VersionGate
{
    private const string RecordKind = "hugo.version";

    private readonly IDeterministicStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly JsonEncodedText _versionPropertyName;
    private readonly byte[] _versionPropertyNameUtf8;

    public VersionGate(IDeterministicStateStore store, TimeProvider? timeProvider = null, JsonSerializerOptions? serializerOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serializerOptions = serializerOptions is null
            ? DeterministicJsonSerializerOptions.Create()
            : DeterministicJsonSerializerOptions.Create(serializerOptions);

        var propertyName = _serializerOptions.PropertyNamingPolicy?.ConvertName(nameof(VersionMarker.Version)) ?? nameof(VersionMarker.Version);
        _versionPropertyName = JsonEncodedText.Encode(propertyName, _serializerOptions.Encoder);
        _versionPropertyNameUtf8 = Encoding.UTF8.GetBytes(propertyName);
    }

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
        _store.Set(changeId, newRecord);

        return Result.Ok(new VersionDecision(decidedVersion, true, now));
    }

    private byte[] SerializeVersion(int version)
    {
        var buffer = new ArrayBufferWriter<byte>(16);
        var writerOptions = new JsonWriterOptions
        {
            Encoder = _serializerOptions.Encoder
        };

        using (var writer = new Utf8JsonWriter(buffer, writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber(_versionPropertyName, version);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private int DeserializeVersion(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("Unable to deserialize persisted version marker.");
        }

        int? version = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals(_versionPropertyNameUtf8) || reader.ValueTextEquals(_versionPropertyName.EncodedUtf8Bytes))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var parsed))
                    {
                        throw new InvalidOperationException("Persisted version marker payload is invalid.");
                    }

                    version = parsed;
                }
                else
                {
                    SkipValue(ref reader);
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
        }

        if (version is null)
        {
            throw new InvalidOperationException("Persisted version marker payload did not contain a version.");
        }

        return version.Value;
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            return;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            var depth = 1;
            while (depth > 0 && reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        break;
                }
            }
        }
    }

    private sealed record VersionMarker(int Version);
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
