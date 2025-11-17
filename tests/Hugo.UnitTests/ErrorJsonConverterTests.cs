using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Shouldly;

namespace Hugo.Tests;

public class ErrorJsonConverterTests
{
    [Fact(Timeout = 15_000)]
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

        deserialized.ShouldNotBeNull();
        deserialized!.Message.ShouldBe("failure");
        deserialized.Code.ShouldBe(ErrorCodes.Validation);
        deserialized.Cause?.Message.ShouldBe("boom");
        deserialized.Metadata.TryGetValue("attempt", out var attempt).ShouldBeTrue();
        attempt.ShouldBe(1L);
        deserialized.Metadata.TryGetValue("duration", out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromSeconds(5));
        deserialized.Metadata.TryGetValue("sub", out var subError).ShouldBeTrue();
        var nested = subError.ShouldBeOfType<Error>();
        nested.Message.ShouldBe("inner");
        deserialized.Metadata.TryGetValue("tags", out var tags).ShouldBeTrue();
        var tagArray = tags.ShouldBeOfType<object?[]>();
        tagArray.OfType<string>().ShouldBe(["one", "two"]);
    }

    [Fact(Timeout = 15_000)]
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

        root.GetProperty("message").GetString().ShouldBe("oops");
        root.GetProperty("code").GetString().ShouldBe(ErrorCodes.Unspecified);
        var metadata = root.GetProperty("metadata");
        metadata.GetProperty("count").GetInt64().ShouldBe(3);
        metadata.GetProperty("timestamp").GetString().ShouldBe("2025-01-01T10:00:00+00:00");
        metadata.GetProperty("inner").GetProperty("message").GetString().ShouldBe("inner");
    }

    [Fact(Timeout = 15_000)]
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

        error.ShouldNotBeNull();
        error!.Metadata.TryGetValue("errors", out var errors).ShouldBeTrue();
        var items = errors.ShouldBeOfType<object?[]>();
        items.Length.ShouldBe(2);
        items[0].ShouldBeOfType<Error>().Message.ShouldBe("first");
        items[1].ShouldBeOfType<Error>().Message.ShouldBe("second");
    }

    [Fact(Timeout = 15_000)]
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

        roundTripped.ShouldNotBeNull();
        roundTripped!.Metadata.TryGetValue("precise", out var preciseValue).ShouldBeTrue();
        var preciseDecimal = preciseValue.ShouldBeOfType<decimal>();
        preciseDecimal.ShouldBe(precise);
        roundTripped.Metadata.TryGetValue("large", out var largeValue).ShouldBeTrue();
        var largeUnsigned = largeValue.ShouldBeOfType<ulong>();
        largeUnsigned.ShouldBe(large);
    }

    [Fact(Timeout = 15_000)]
    public void Deserialize_ShouldThrowWhenRootIsNotObject()
    {
        const string json = """["not", "an", "object"]""";

        Should.Throw<JsonException>(static () => JsonSerializer.Deserialize<Error>(json));
    }

    [Theory]
    [InlineData("""{"code": "oops"}""")]
    [InlineData("""{"message": null}""")]
    [InlineData("""{"message": 42}""")]
    public void Deserialize_ShouldRequireStringMessage(string json) => Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Error>(json));

    [Fact(Timeout = 15_000)]
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

        error.ShouldNotBeNull();
        var cause = error!.Cause;
        cause.ShouldNotBeNull();
        cause!.Message.ShouldBe("boom");
        cause.GetType().Name.ShouldBe("SerializedErrorException");
        var typeNameProperty = cause.GetType().GetProperty("TypeName");
        typeNameProperty.ShouldNotBeNull();
        typeNameProperty!.GetValue(cause).ShouldBe("System.InvalidOperationException");
        cause.StackTrace.ShouldBe("at Some.Method()\n   at Other.Method()");
    }

    [Fact(Timeout = 15_000)]
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

        error.ShouldNotBeNull();
        error!.Metadata.TryGetValue("guid", out var guidValue).ShouldBeTrue();
        guidValue.ShouldBeOfType<Guid>().ShouldBe(guid);
        error.Metadata.TryGetValue("timespan", out var timeSpanValue).ShouldBeTrue();
        timeSpanValue.ShouldBeOfType<TimeSpan>().ShouldBe(TimeSpan.Parse("1.02:03:04", CultureInfo.InvariantCulture));
        error.Metadata.TryGetValue("offset", out var offsetValue).ShouldBeTrue();
        offsetValue.ShouldBeOfType<DateTimeOffset>().ShouldBe(offset);
        error.Metadata.TryGetValue("utcDate", out var utcValue).ShouldBeTrue();
        utcValue.ShouldBeOfType<DateTimeOffset>().ShouldBe(utc);
        error.Metadata.TryGetValue("plain", out var plainValue).ShouldBeTrue();
        plainValue.ShouldBeOfType<string>().ShouldBe("value");
    }

    [Fact(Timeout = 15_000)]
    public void Serialize_ShouldPreserveNestedMetadataObjects()
    {
        var error = Error.From("outer", metadata: new Dictionary<string, object?>
        {
            ["container"] = new Dictionary<string, object?>
            {
                ["child"] = Error.From("inner")
            }
        });

        var json = JsonSerializer.Serialize(error);
        using var document = JsonDocument.Parse(json);
        var container = document.RootElement.GetProperty("metadata").GetProperty("container");

        container.GetProperty("child").GetProperty("message").GetString().ShouldBe("inner");
    }


    [Fact(Timeout = 15_000)]
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
        causeElement.GetProperty("type").GetString().ShouldBe(typeof(InvalidOperationException).FullName);
        causeElement.GetProperty("message").GetString().ShouldBe("fails here");
        string.IsNullOrEmpty(causeElement.GetProperty("stackTrace").GetString()).ShouldBeFalse();

        var meta = root.GetProperty("metadata");
        meta.TryGetProperty("jsonElement", out var jsonElement).ShouldBeTrue();
        jsonElement.GetProperty("flag").GetBoolean().ShouldBeTrue();
        jsonElement.GetProperty("number").GetInt32().ShouldBe(7);
        meta.GetProperty("readOnlyDictionary").GetProperty("nested").GetInt32().ShouldBe(5);
        meta.GetProperty("dictionary").GetProperty("hash").GetString().ShouldBe("value");
        var enumeratedItems = meta.GetProperty("enumerable")
            .EnumerateArray()
            .Select(static e => e.GetInt32())
            .ToArray();
        enumeratedItems.ShouldBe([1, 2, 3]);
        var serializedErrors = meta.GetProperty("errorEnumerable").EnumerateArray().Select(static e => e.GetProperty("message").GetString()).ToArray();
        serializedErrors.ShouldBe(new[] { "first", "second" });
        meta.GetProperty("unsupported").GetString().ShouldBe(unsupportedDescription);
        meta.GetProperty("date").GetString().ShouldBe("2024-05-01T10:15:30Z");
        meta.GetProperty("guid").GetString().ShouldBe("d742c49b-bb76-4a0f-9ef0-2f9f11bef0df");
        meta.GetProperty("boolean").GetBoolean().ShouldBeTrue();
        meta.GetProperty("null").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact(Timeout = 15_000)]
    public void Serialize_ShouldSanitizeCancellationTokenMetadata()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = Error.Canceled(token: cts.Token);

        var json = JsonSerializer.Serialize(error);
        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement.GetProperty("metadata");
        metadata.TryGetProperty("cancellationToken", out var tokenMetadata).ShouldBeTrue();
        tokenMetadata.GetProperty("canBeCanceled").GetBoolean().ShouldBeTrue();
        tokenMetadata.GetProperty("isCancellationRequested").GetBoolean().ShouldBeTrue();
    }
}
