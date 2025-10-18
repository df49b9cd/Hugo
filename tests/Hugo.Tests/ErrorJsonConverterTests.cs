using System;
using System.Collections.Generic;
using System.Linq;
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
        Assert.Equal(new[] { "one", "two" }, tagArray.OfType<string>());
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
            item => Assert.Equal("first", Assert.IsType<Error>(item).Message),
            item => Assert.Equal("second", Assert.IsType<Error>(item).Message));
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
}
