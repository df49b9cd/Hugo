using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hugo;

internal sealed class ErrorJsonConverter : JsonConverter<Error>
{
    public override Error? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Error payload must be an object.");
        }

        if (!root.TryGetProperty("message", out var messageProperty) || messageProperty.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Error payload must contain a string 'message' property.");
        }

        var message = messageProperty.GetString() ?? throw new JsonException("Error message cannot be null.");
        string? code = null;
        if (root.TryGetProperty("code", out var codeProperty) && codeProperty.ValueKind != JsonValueKind.Null)
        {
            code = codeProperty.GetString();
        }

        Exception? cause = null;
        if (root.TryGetProperty("cause", out var causeProperty) && causeProperty.ValueKind == JsonValueKind.Object)
        {
            var typeName = causeProperty.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String
                ? typeProperty.GetString()
                : null;
            var causeMessage = causeProperty.TryGetProperty("message", out var causeMessageProperty) && causeMessageProperty.ValueKind == JsonValueKind.String
                ? causeMessageProperty.GetString()
                : null;
            var stackTrace = causeProperty.TryGetProperty("stackTrace", out var stackProperty) && stackProperty.ValueKind == JsonValueKind.String
                ? stackProperty.GetString()
                : null;

            cause = new SerializedErrorException(typeName, causeMessage, stackTrace);
        }

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("metadata", out var metadataProperty) && metadataProperty.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in metadataProperty.EnumerateObject())
            {
                metadata[item.Name] = DeserializeMetadataValue(item.Value, options);
            }
        }

        return Error.From(message, code, cause, metadata);
    }

    public override void Write(Utf8JsonWriter writer, Error value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("message", value.Message);
        if (!string.IsNullOrEmpty(value.Code))
        {
            writer.WriteString("code", value.Code);
        }

        if (value.Cause is { } cause)
        {
            writer.WritePropertyName("cause");
            writer.WriteStartObject();
            writer.WriteString("type", cause.GetType().FullName ?? cause.GetType().Name);
            writer.WriteString("message", cause.Message);
            var stackTrace = cause.StackTrace;
            if (!string.IsNullOrEmpty(stackTrace))
            {
                writer.WriteString("stackTrace", stackTrace);
            }
            writer.WriteEndObject();
        }

        if (value.Metadata.Count > 0)
        {
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            foreach (var kvp in value.Metadata)
            {
                writer.WritePropertyName(kvp.Key);
                WriteMetadataValue(writer, kvp.Value, options);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteMetadataValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case byte b8:
                writer.WriteNumberValue(b8);
                return;
            case sbyte sb:
                writer.WriteNumberValue(sb);
                return;
            case short s16:
                writer.WriteNumberValue(s16);
                return;
            case ushort us16:
                writer.WriteNumberValue(us16);
                return;
            case int i32:
                writer.WriteNumberValue(i32);
                return;
            case uint ui32:
                writer.WriteNumberValue(ui32);
                return;
            case long i64:
                writer.WriteNumberValue(i64);
                return;
            case ulong ui64:
                writer.WriteNumberValue(ui64);
                return;
            case float f32:
                writer.WriteNumberValue(f32);
                return;
            case double f64:
                writer.WriteNumberValue(f64);
                return;
            case decimal dec:
                writer.WriteNumberValue(dec);
                return;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                return;
            case Guid guid:
                writer.WriteStringValue(guid);
                return;
            case TimeSpan timeSpan:
                writer.WriteStringValue(timeSpan.ToString("c", CultureInfo.InvariantCulture));
                return;
            case Error error:
                JsonSerializer.Serialize(writer, error, options);
                return;
            case IEnumerable<Error> errorEnumerable:
                writer.WriteStartArray();
                foreach (var err in errorEnumerable)
                {
                    JsonSerializer.Serialize(writer, err, options);
                }
                writer.WriteEndArray();
                return;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                writer.WriteStartObject();
                foreach (var entry in readOnlyDictionary)
                {
                    writer.WritePropertyName(entry.Key);
                    WriteMetadataValue(writer, entry.Value, options);
                }
                writer.WriteEndObject();
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName(entry.Key?.ToString() ?? string.Empty);
                    WriteMetadataValue(writer, entry.Value, options);
                }
                writer.WriteEndObject();
                return;
            case IEnumerable enumerable and not string:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteMetadataValue(writer, item, options);
                }
                writer.WriteEndArray();
                return;
            default:
                try
                {
                    JsonSerializer.Serialize(writer, value, value.GetType(), options);
                }
                catch (NotSupportedException)
                {
                    writer.WriteStringValue(value.ToString());
                }

                return;
        }
    }

    private static object? DeserializeMetadataValue(JsonElement element, JsonSerializerOptions options)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => TryDeserializeNumber(element),
            JsonValueKind.String => ParseStringMetadata(element.GetString()),
            JsonValueKind.Array => DeserializeArray(element, options),
            JsonValueKind.Object when LooksLikeError(element) => JsonSerializer.Deserialize<Error>(element.GetRawText(), options),
            JsonValueKind.Object => DeserializeObject(element, options),
            _ => element.GetRawText()
        };
    }

    private static object? TryDeserializeNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var int64))
        {
            return int64;
        }

        if (element.TryGetUInt64(out var uint64))
        {
            return uint64;
        }

        if (element.TryGetDecimal(out var @decimal))
        {
            return @decimal;
        }

        if (element.TryGetDouble(out var @double))
        {
            return @double;
        }

        return element.GetRawText();
    }

    private static object? ParseStringMetadata(string? value)
    {
        if (value is null)
            return null;

        if (TimeSpan.TryParseExact(value, ["c", "g", "G"], CultureInfo.InvariantCulture, out var timeSpan))
            return timeSpan;

        if (Guid.TryParse(value, out var guid))
            return guid;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTimeOffset))
            return dateTimeOffset;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
            return dateTime;

        return value;
    }

    private static object? DeserializeArray(JsonElement element, JsonSerializerOptions options)
    {
        var list = new List<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && LooksLikeError(item))
            {
                var nested = JsonSerializer.Deserialize<Error>(item.GetRawText(), options);
                list.Add(nested);
            }
            else
            {
                list.Add(DeserializeMetadataValue(item, options));
            }
        }

        return list.ToArray();
    }

    private static object DeserializeObject(JsonElement element, JsonSerializerOptions options)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = DeserializeMetadataValue(property.Value, options);
        }

        return dictionary;
    }

    private static bool LooksLikeError(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("message", out var messageProperty)
            && messageProperty.ValueKind == JsonValueKind.String;
    }

    private sealed class SerializedErrorException : Exception
    {
        private readonly string? _stackTrace;

        public SerializedErrorException(string? typeName, string? message, string? stackTrace)
            : base(message ?? typeName ?? nameof(SerializedErrorException))
        {
            TypeName = typeName;
            _stackTrace = stackTrace;
        }

        public string? TypeName { get; }

        public override string? StackTrace => _stackTrace ?? base.StackTrace;
    }
}
