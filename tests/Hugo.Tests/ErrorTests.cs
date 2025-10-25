namespace Hugo.Tests;

public class ErrorTests
{
    [Fact]
    public void WithMetadata_ShouldAddEntry()
    {
        var error = Error.From("boom", ErrorCodes.Exception);
        var enriched = error.WithMetadata("key", 123);

        Assert.Equal(error.Message, enriched.Message);
        Assert.True(enriched.Metadata.ContainsKey("key"));
        Assert.Equal(123, enriched.Metadata["key"]);
    }

    [Fact]
    public void WithMetadata_ShouldThrow_WhenKeyIsInvalid()
    {
        var error = Error.From("message");

        Assert.Throws<ArgumentException>(() => error.WithMetadata(" ", 1));
    }

    [Fact]
    public void WithMetadataCollection_ShouldMergeCaseInsensitive()
    {
        var error = Error.From("boom").WithMetadata("Key", 1);
        var enriched = error.WithMetadata([new KeyValuePair<string, object?>("key", 2)]);

        Assert.Equal(2, enriched.Metadata["key"]);
    }

    [Fact]
    public void WithMetadataCollection_ShouldThrow_WhenMetadataIsNull()
    {
        var error = Error.From("message");

        Assert.Throws<ArgumentNullException>(() => error.WithMetadata((IEnumerable<KeyValuePair<string, object?>>)null!));
    }

    [Fact]
    public void WithCode_ShouldReturnNewInstance()
    {
        var error = Error.From("message");
        var reclassified = error.WithCode(ErrorCodes.Validation);

        Assert.Equal(ErrorCodes.Validation, reclassified.Code);
        Assert.Null(error.Code);
    }

    [Fact]
    public void WithCause_ShouldAttachException()
    {
        var error = Error.From("message");
        var cause = new InvalidOperationException("oops");
        var enriched = error.WithCause(cause);

        Assert.Same(cause, enriched.Cause);
    }

    [Fact]
    public void FromException_ShouldThrow_WhenExceptionIsNull() => Assert.Throws<ArgumentNullException>(static () => Error.FromException(null!));

    [Fact]
    public void TryGetMetadata_ShouldReturnValue()
    {
        var error = Error.From("message").WithMetadata("count", 5);

        Assert.True(error.TryGetMetadata("count", out int value));
        Assert.Equal(5, value);
    }

    [Fact]
    public void TryGetMetadata_ShouldReturnFalseWhenMissing()
    {
        var error = Error.From("message");

        Assert.False(error.TryGetMetadata("missing", out int _));
    }

    [Fact]
    public void Timeout_ShouldIncludeDuration()
    {
        var duration = TimeSpan.FromSeconds(5);
        var error = Error.Timeout(duration);

        Assert.Equal(ErrorCodes.Timeout, error.Code);
        Assert.True(error.Metadata.TryGetValue("duration", out var value));
        Assert.Equal(duration, value);
    }

    [Fact]
    public void Canceled_WithToken_ShouldCaptureMetadata()
    {
        using var cts = new CancellationTokenSource();
        var error = Error.Canceled(token: cts.Token);

        Assert.Equal(ErrorCodes.Canceled, error.Code);
        Assert.True(error.Metadata.ContainsKey("cancellationToken"));
        Assert.IsType<OperationCanceledException>(error.Cause);
    }

    [Fact]
    public void Aggregate_ShouldIncludeChildErrors()
    {
        var first = Error.From("one");
        var second = Error.From("two");
        var aggregate = Error.Aggregate("many", first, second);

        Assert.Equal(ErrorCodes.Aggregate, aggregate.Code);
        Assert.True(aggregate.Metadata.TryGetValue("errors", out var value));
        Assert.Contains(first, (Error[])value!);
        Assert.Contains(second, (Error[])value!);
    }
}
