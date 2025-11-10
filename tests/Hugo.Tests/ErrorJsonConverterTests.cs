using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Hugo.Tests;

public class ErrorJsonConverterTests
{
    [Fact]
    public void SerializeAndDeserialize_ShouldRoundTripError()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["attempt"] = 1,
            ["duration"] = TimeSpan.FromSeconds(5),
            ["sub"] = Error.From("inner"),
            ["tags"] = new[] { "one", "two" }
        };
        var original = Error.From("failure", ErrorCodes.Validation, metadata: metadata)
            .WithCause(new InvalidOperationException("boom"));

        var options = new JsonSerializerOptions { WriteIndented = false };
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<Error>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal("failure", deserialized!.Message);
        Assert.Equal(ErrorCodes.Validation, deserialized.Code);
        Assert.Equal("boom", deserialized.Cause?.Message);
        Assert.True(deserialized.Metadata.TryGetValue("attempt", out var attempt));
        Assert.Equal(1L, attempt);
        Assert.True(deserialized.Metadata.TryGetValue("duration", out var duration));
        Assert.Equal(TimeSpan.FromSeconds(5), duration);
        Assert.True(deserialized.Metadata.TryGetValue("sub", out var subError));
        var nested = Assert.IsType<Error>(subError);
        Assert.Equal("inner", nested.Message);
        Assert.True(deserialized.Metadata.TryGetValue("tags", out var tags));
        var tagArray = Assert.IsType<object?[]>(tags);
        Assert.Equal(["one", "two"], tagArray.OfType<string>());
    }

    [Fact]
    public void Serialize_ShouldEmitJsonStructure()
    {
        var error = Error.From("oops", ErrorCodes.Unspecified, metadata: new Dictionary<string, object?>
        {
            ["count"] = 3,
            ["timestamp"] = DateTimeOffset.Parse("2025-01-01T10:00:00Z"),
            ["inner"] = Error.From("inner")
        });

        var json = JsonSerializer.Serialize(error);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("oops", root.GetProperty("message").GetString());
        Assert.Equal(ErrorCodes.Unspecified, root.GetProperty("code").GetString());
        var metadata = root.GetProperty("metadata");
        Assert.Equal(3, metadata.GetProperty("count").GetInt64());
        Assert.Equal("2025-01-01T10:00:00+00:00", metadata.GetProperty("timestamp").GetString());
        Assert.Equal("inner", metadata.GetProperty("inner").GetProperty("message").GetString());
    }

    [Fact]
    public void Deserialize_ShouldHandleNestedAggregateErrors()
    {
        var json = """
        {
            "message": "failure",
            "metadata": {
                "errors": [
                    { "message": "first", "metadata": {} },
                    { "message": "second", "metadata": {} }
                ]
            }
        }
        """;

        var error = JsonSerializer.Deserialize<Error>(json);

        Assert.NotNull(error);
        Assert.True(error!.Metadata.TryGetValue("errors", out var errors));
        var items = Assert.IsType<object?[]>(errors);
        Assert.Collection(items,
            static item => Assert.Equal("first", Assert.IsType<Error>(item).Message),
            static item => Assert.Equal("second", Assert.IsType<Error>(item).Message));
    }

    [Fact]
    public void SerializeAndDeserialize_ShouldPreserveDecimalAndUnsignedNumericMetadata()
    {
        var precise = 1234567890.1234567890123456789M;
        var large = ulong.MaxValue;
        var error = Error.From("numeric", metadata: new Dictionary<string, object?>
        {
            ["precise"] = precise,
            ["large"] = large
        });

        var options = new JsonSerializerOptions();
        var json = JsonSerializer.Serialize(error, options);
        var roundTripped = JsonSerializer.Deserialize<Error>(json, options);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.Metadata.TryGetValue("precise", out var preciseValue));
        var preciseDecimal = Assert.IsType<decimal>(preciseValue);
        Assert.Equal(precise, preciseDecimal);
        Assert.True(roundTripped.Metadata.TryGetValue("large", out var largeValue));
        var largeUnsigned = Assert.IsType<ulong>(largeValue);
        Assert.Equal(large, largeUnsigned);
    }

    [Fact]
    public void Deserialize_ShouldThrowWhenRootIsNotObject()
    {
        const string json = """["not", "an", "object"]""";

        Assert.Throws<JsonException>(static () => JsonSerializer.Deserialize<Error>(json));
    }

    [Theory]
    [InlineData("""{"code": "oops"}""")]
    [InlineData("""{"message": null}""")]
    [InlineData("""{"message": 42}""")]
    public void Deserialize_ShouldRequireStringMessage(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Error>(json));
    }

    [Fact]
    public void Deserialize_ShouldHydrateCauseExceptionDetails()
    {
        const string json = """
        {
            "message": "outer",
            "cause": {
                "type": "System.InvalidOperationException",
                "message": "boom",
                "stackTrace": "at Some.Method()\n   at Other.Method()"
            }
        }
        """;

        var error = JsonSerializer.Deserialize<Error>(json);

        Assert.NotNull(error);
        var cause = error!.Cause;
        Assert.NotNull(cause);
        Assert.Equal("boom", cause!.Message);
        Assert.Equal("SerializedErrorException", cause.GetType().Name);
        var typeNameProperty = cause.GetType().GetProperty("TypeName");
        Assert.NotNull(typeNameProperty);
        Assert.Equal("System.InvalidOperationException", typeNameProperty!.GetValue(cause));
        Assert.Equal("at Some.Method()\n   at Other.Method()", cause.StackTrace);
    }

    [Fact]
    public void Deserialize_ShouldInterpretWellKnownStringMetadataTypes()
    {
        var guid = Guid.NewGuid();
        var offset = DateTimeOffset.Parse("2024-05-01T10:15:30+02:00", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var utc = DateTimeOffset.Parse("2024-05-01T10:15:30Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var json = $$"""
        {
            "message": "formatted",
            "metadata": {
                "guid": "{{guid}}",
                "timespan": "1.02:03:04",
                "offset": "2024-05-01T10:15:30+02:00",
                "utcDate": "2024-05-01T10:15:30Z",
                "plain": "value"
            }
        }
        """;

        var error = JsonSerializer.Deserialize<Error>(json);

        Assert.NotNull(error);
        Assert.True(error!.Metadata.TryGetValue("guid", out var guidValue));
        Assert.Equal(guid, Assert.IsType<Guid>(guidValue));
        Assert.True(error.Metadata.TryGetValue("timespan", out var timeSpanValue));
        Assert.Equal(TimeSpan.Parse("1.02:03:04", CultureInfo.InvariantCulture), Assert.IsType<TimeSpan>(timeSpanValue));
        Assert.True(error.Metadata.TryGetValue("offset", out var offsetValue));
        Assert.Equal(offset, Assert.IsType<DateTimeOffset>(offsetValue));
        Assert.True(error.Metadata.TryGetValue("utcDate", out var utcValue));
        Assert.Equal(utc, Assert.IsType<DateTimeOffset>(utcValue));
        Assert.True(error.Metadata.TryGetValue("plain", out var plainValue));
        Assert.Equal("value", Assert.IsType<string>(plainValue));
    }


    [Fact]
    public void Serialize_ShouldHandleComplexMetadataShapes()
    {
        using var jsonDoc = JsonDocument.Parse("""{"flag": true, "number": 7}""");
        var unsupported = typeof(System.IO.Stream);
        var unsupportedDescription = unsupported.ToString();
        var errors = new List<Error> { Error.From("first"), Error.From("second") };
        InvalidOperationException invalidOperation;
        try
        {
            throw new InvalidOperationException("fails here");
        }
        catch (InvalidOperationException ex)
        {
            invalidOperation = ex;
        }
        var metadata = new Dictionary<string, object?>
        {
            ["jsonElement"] = jsonDoc.RootElement.Clone(),
            ["readOnlyDictionary"] = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["nested"] = 5 }),
            ["dictionary"] = new Hashtable { ["hash"] = "value" },
            ["enumerable"] = new List<int> { 1, 2, 3 },
            ["errorEnumerable"] = errors,
            ["unsupported"] = unsupported,
            ["date"] = new DateTime(2024, 05, 01, 10, 15, 30, DateTimeKind.Utc),
            ["guid"] = Guid.Parse("d742c49b-bb76-4a0f-9ef0-2f9f11bef0df"),
            ["boolean"] = true,
            ["null"] = null
        };
        var error = Error.From("meta", metadata: metadata).WithCause(invalidOperation);

        var json = JsonSerializer.Serialize(error, new JsonSerializerOptions { WriteIndented = false });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var causeElement = root.GetProperty("cause");
        Assert.Equal(typeof(InvalidOperationException).FullName, causeElement.GetProperty("type").GetString());
        Assert.Equal("fails here", causeElement.GetProperty("message").GetString());
        Assert.False(string.IsNullOrEmpty(causeElement.GetProperty("stackTrace").GetString()));

        var meta = root.GetProperty("metadata");
        Assert.True(meta.TryGetProperty("jsonElement", out var jsonElement));
        Assert.True(jsonElement.GetProperty("flag").GetBoolean());
        Assert.Equal(7, jsonElement.GetProperty("number").GetInt32());
        Assert.Equal(5, meta.GetProperty("readOnlyDictionary").GetProperty("nested").GetInt32());
        Assert.Equal("value", meta.GetProperty("dictionary").GetProperty("hash").GetString());
        var enumeratedItems = meta.GetProperty("enumerable")
            .EnumerateArray()
            .Select(static e => e.GetInt32())
            .ToArray();
        Assert.Equal([1, 2, 3], enumeratedItems);
        var serializedErrors = meta.GetProperty("errorEnumerable").EnumerateArray().Select(static e => e.GetProperty("message").GetString()).ToArray();
        Assert.Equal(new[] { "first", "second" }, serializedErrors);
        Assert.Equal(unsupportedDescription, meta.GetProperty("unsupported").GetString());
        Assert.Equal("2024-05-01T10:15:30Z", meta.GetProperty("date").GetString());
        Assert.Equal("d742c49b-bb76-4a0f-9ef0-2f9f11bef0df", meta.GetProperty("guid").GetString());
        Assert.True(meta.GetProperty("boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("null").ValueKind);
    }

    [Fact]
    public void Serialize_ShouldSanitizeCancellationTokenMetadata()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = Error.Canceled(token: cts.Token);

        var json = JsonSerializer.Serialize(error);
        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.True(metadata.TryGetProperty("cancellationToken", out var tokenMetadata));
        Assert.True(tokenMetadata.GetProperty("canBeCanceled").GetBoolean());
        Assert.True(tokenMetadata.GetProperty("isCancellationRequested").GetBoolean());
    }
}
