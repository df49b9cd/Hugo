using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Helpers for configuring Native AOT-friendly serialization of <see cref="TaskQueueReplicationEvent{T}"/>.
/// </summary>
public static class TaskQueueReplicationJsonSerialization
{
    /// <summary>
    /// Creates <see cref="JsonSerializerOptions"/> that include converters for <see cref="TaskQueueReplicationEvent{T}"/>.
    /// Caller must provide a <see cref="JsonSerializerContext"/> that exposes metadata for <typeparamref name="T"/>.
    /// </summary>
    public static JsonSerializerOptions CreateOptions<T>(JsonSerializerContext context, JsonSerializerOptions? template = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = template is null ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions(template);
        var valueInfo = context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
                        ?? throw new InvalidOperationException($"The supplied JsonSerializerContext '{context.GetType().Name}' does not expose metadata for '{typeof(T)}'.");

        EnsureConverter(options, valueInfo);
        return options;
    }

    /// <summary>
    /// Adds the replication converter to existing options using caller-supplied metadata for the payload type.
    /// </summary>
    public static void EnsureConverter<T>(JsonSerializerOptions options, JsonTypeInfo<T> valueTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(valueTypeInfo);

        foreach (JsonConverter converter in options.Converters)
        {
            if (converter is TaskQueueReplicationEventJsonConverter<T>)
            {
                return;
            }
        }

        var replicationMetadata = TaskQueueReplicationMetadataContext.Default;
        var errorInfo = DeterministicJsonSerialization.DefaultContext.GetTypeInfo<Error>() as JsonTypeInfo<Error>
                        ?? throw new InvalidOperationException("DeterministicJsonSerialization context did not expose Error metadata.");

        options.Converters.Add(new TaskQueueReplicationEventJsonConverter<T>(
            valueTypeInfo,
            replicationMetadata.TaskQueueReplicationEventKind,
            errorInfo,
            replicationMetadata.TaskQueueOwnershipToken,
            replicationMetadata.TaskQueueLifecycleEventMetadata));
    }

    private sealed class TaskQueueReplicationEventJsonConverter<T> : JsonConverter<TaskQueueReplicationEvent<T>>
    {
        private readonly JsonTypeInfo<T> _valueInfo;
        private readonly JsonTypeInfo<TaskQueueReplicationEventKind> _kindInfo;
        private readonly JsonTypeInfo<Error> _errorInfo;
        private readonly JsonTypeInfo<TaskQueueOwnershipToken> _ownershipInfo;
        private readonly JsonTypeInfo<TaskQueueLifecycleEventMetadata> _flagsInfo;

        public TaskQueueReplicationEventJsonConverter(
            JsonTypeInfo<T> valueInfo,
            JsonTypeInfo<TaskQueueReplicationEventKind> kindInfo,
            JsonTypeInfo<Error> errorInfo,
            JsonTypeInfo<TaskQueueOwnershipToken> ownershipInfo,
            JsonTypeInfo<TaskQueueLifecycleEventMetadata> flagsInfo)
        {
            _valueInfo = valueInfo;
            _kindInfo = kindInfo;
            _errorInfo = errorInfo;
            _ownershipInfo = ownershipInfo;
            _flagsInfo = flagsInfo;
        }

        private static readonly JsonEncodedText SequenceNumberProp = JsonEncodedText.Encode("sequenceNumber");
        private static readonly JsonEncodedText SourceEventIdProp = JsonEncodedText.Encode("sourceEventId");
        private static readonly JsonEncodedText QueueNameProp = JsonEncodedText.Encode("queueName");
        private static readonly JsonEncodedText KindProp = JsonEncodedText.Encode("kind");
        private static readonly JsonEncodedText SourcePeerIdProp = JsonEncodedText.Encode("sourcePeerId");
        private static readonly JsonEncodedText OwnerPeerIdProp = JsonEncodedText.Encode("ownerPeerId");
        private static readonly JsonEncodedText OccurredAtProp = JsonEncodedText.Encode("occurredAt");
        private static readonly JsonEncodedText RecordedAtProp = JsonEncodedText.Encode("recordedAt");
        private static readonly JsonEncodedText TaskSequenceIdProp = JsonEncodedText.Encode("taskSequenceId");
        private static readonly JsonEncodedText AttemptProp = JsonEncodedText.Encode("attempt");
        private static readonly JsonEncodedText ValueProp = JsonEncodedText.Encode("value");
        private static readonly JsonEncodedText ErrorProp = JsonEncodedText.Encode("error");
        private static readonly JsonEncodedText OwnershipTokenProp = JsonEncodedText.Encode("ownershipToken");
        private static readonly JsonEncodedText LeaseExpirationProp = JsonEncodedText.Encode("leaseExpiration");
        private static readonly JsonEncodedText FlagsProp = JsonEncodedText.Encode("flags");

        private static ReadOnlySpan<byte> SequenceNumberName => "sequenceNumber"u8;
        private static ReadOnlySpan<byte> SourceEventIdName => "sourceEventId"u8;
        private static ReadOnlySpan<byte> QueueNameName => "queueName"u8;
        private static ReadOnlySpan<byte> KindName => "kind"u8;
        private static ReadOnlySpan<byte> SourcePeerIdName => "sourcePeerId"u8;
        private static ReadOnlySpan<byte> OwnerPeerIdName => "ownerPeerId"u8;
        private static ReadOnlySpan<byte> OccurredAtName => "occurredAt"u8;
        private static ReadOnlySpan<byte> RecordedAtName => "recordedAt"u8;
        private static ReadOnlySpan<byte> TaskSequenceIdName => "taskSequenceId"u8;
        private static ReadOnlySpan<byte> AttemptName => "attempt"u8;
        private static ReadOnlySpan<byte> ValueName => "value"u8;
        private static ReadOnlySpan<byte> ErrorName => "error"u8;
        private static ReadOnlySpan<byte> OwnershipTokenName => "ownershipToken"u8;
        private static ReadOnlySpan<byte> LeaseExpirationName => "leaseExpiration"u8;
        private static ReadOnlySpan<byte> FlagsName => "flags"u8;

        public override TaskQueueReplicationEvent<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected start of object for TaskQueueReplicationEvent.");
            }

            long sequenceNumber = default;
            long sourceEventId = default;
            string? queueName = null;
            TaskQueueReplicationEventKind kind = default;
            string? sourcePeerId = null;
            string? ownerPeerId = null;
            DateTimeOffset occurredAt = default;
            DateTimeOffset recordedAt = default;
            long taskSequenceId = default;
            int attempt = default;
            T? value = default;
            Error? error = null;
            TaskQueueOwnershipToken? ownershipToken = null;
            DateTimeOffset? leaseExpiration = null;
            TaskQueueLifecycleEventMetadata flags = default;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected property name.");
                }

                if (reader.ValueTextEquals(SequenceNumberName))
                {
                    reader.Read();
                    sequenceNumber = reader.GetInt64();
                    continue;
                }
                if (reader.ValueTextEquals(SourceEventIdName))
                {
                    reader.Read();
                    sourceEventId = reader.GetInt64();
                    continue;
                }
                if (reader.ValueTextEquals(QueueNameName))
                {
                    reader.Read();
                    queueName = reader.GetString();
                    continue;
                }
                if (reader.ValueTextEquals(KindName))
                {
                    reader.Read();
                    kind = JsonSerializer.Deserialize(ref reader, _kindInfo);
                    continue;
                }
                if (reader.ValueTextEquals(SourcePeerIdName))
                {
                    reader.Read();
                    sourcePeerId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    continue;
                }
                if (reader.ValueTextEquals(OwnerPeerIdName))
                {
                    reader.Read();
                    ownerPeerId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    continue;
                }
                if (reader.ValueTextEquals(OccurredAtName))
                {
                    reader.Read();
                    occurredAt = reader.GetDateTimeOffset();
                    continue;
                }
                if (reader.ValueTextEquals(RecordedAtName))
                {
                    reader.Read();
                    recordedAt = reader.GetDateTimeOffset();
                    continue;
                }
                if (reader.ValueTextEquals(TaskSequenceIdName))
                {
                    reader.Read();
                    taskSequenceId = reader.GetInt64();
                    continue;
                }
                if (reader.ValueTextEquals(AttemptName))
                {
                    reader.Read();
                    attempt = reader.GetInt32();
                    continue;
                }
                if (reader.ValueTextEquals(ValueName))
                {
                    reader.Read();
                    value = JsonSerializer.Deserialize(ref reader, _valueInfo);
                    continue;
                }
                if (reader.ValueTextEquals(ErrorName))
                {
                    reader.Read();
                    error = reader.TokenType == JsonTokenType.Null ? null : JsonSerializer.Deserialize(ref reader, _errorInfo);
                    continue;
                }
                if (reader.ValueTextEquals(OwnershipTokenName))
                {
                    reader.Read();
                    ownershipToken = reader.TokenType == JsonTokenType.Null ? null : JsonSerializer.Deserialize(ref reader, _ownershipInfo);
                    continue;
                }
                if (reader.ValueTextEquals(LeaseExpirationName))
                {
                    reader.Read();
                    leaseExpiration = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();
                    continue;
                }
                if (reader.ValueTextEquals(FlagsName))
                {
                    reader.Read();
                    flags = JsonSerializer.Deserialize(ref reader, _flagsInfo);
                    continue;
                }

                reader.Skip();
            }

            if (queueName is null)
            {
                throw new JsonException("queueName was not provided.");
            }

            return new TaskQueueReplicationEvent<T>(
                sequenceNumber,
                sourceEventId,
                queueName,
                kind,
                sourcePeerId,
                ownerPeerId,
                occurredAt,
                recordedAt,
                taskSequenceId,
                attempt,
                value,
                error,
                ownershipToken,
                leaseExpiration,
                flags);
        }

        public override void Write(Utf8JsonWriter writer, TaskQueueReplicationEvent<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(SequenceNumberProp, value.SequenceNumber);
            writer.WriteNumber(SourceEventIdProp, value.SourceEventId);
            writer.WriteString(QueueNameProp, value.QueueName);
            writer.WritePropertyName(KindProp);
            JsonSerializer.Serialize(writer, value.Kind, _kindInfo);

            writer.WritePropertyName(SourcePeerIdProp);
            if (value.SourcePeerId is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.SourcePeerId);
            }

            writer.WritePropertyName(OwnerPeerIdProp);
            if (value.OwnerPeerId is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.OwnerPeerId);
            }

            writer.WriteString(OccurredAtProp, value.OccurredAt);
            writer.WriteString(RecordedAtProp, value.RecordedAt);
            writer.WriteNumber(TaskSequenceIdProp, value.TaskSequenceId);
            writer.WriteNumber(AttemptProp, value.Attempt);

            writer.WritePropertyName(ValueProp);
            JsonSerializer.Serialize(writer, value.Value, _valueInfo);

            writer.WritePropertyName(ErrorProp);
            if (value.Error is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value.Error, _errorInfo);
            }

            writer.WritePropertyName(OwnershipTokenProp);
            if (value.OwnershipToken is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value.OwnershipToken, _ownershipInfo);
            }

            writer.WritePropertyName(LeaseExpirationProp);
            if (value.LeaseExpiration is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.LeaseExpiration.Value);
            }

            writer.WritePropertyName(FlagsProp);
            JsonSerializer.Serialize(writer, value.Flags, _flagsInfo);

            writer.WriteEndObject();
        }
    }
}
