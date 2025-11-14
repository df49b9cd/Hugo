namespace Hugo.Tests;

public class ErrorTests
{
    [Fact(Timeout = 15_000)]
    public void WithMetadata_ShouldAddEntry()
    {
        var error = Error.From("boom", ErrorCodes.Exception);
        var enriched = error.WithMetadata("key", 123);

        Assert.Equal(error.Message, enriched.Message);
        Assert.True(enriched.Metadata.ContainsKey("key"));
        Assert.Equal(123, enriched.Metadata["key"]);
    }

    [Fact(Timeout = 15_000)]
    public void WithMetadata_ShouldThrow_WhenKeyIsInvalid()
    {
        var error = Error.From("message");

        Assert.Throws<ArgumentException>(() => error.WithMetadata(" ", 1));
    }

    [Fact(Timeout = 15_000)]
    public void WithMetadataCollection_ShouldMergeCaseInsensitive()
    {
        var error = Error.From("boom").WithMetadata("Key", 1);
        var enriched = error.WithMetadata([new KeyValuePair<string, object?>("key", 2)]);

        Assert.Equal(2, enriched.Metadata["key"]);
    }

    [Fact(Timeout = 15_000)]
    public void WithMetadataCollection_ShouldThrow_WhenMetadataIsNull()
    {
        var error = Error.From("message");

        Assert.Throws<ArgumentNullException>(() => error.WithMetadata((IEnumerable<KeyValuePair<string, object?>>)null!));
    }

    [Fact(Timeout = 15_000)]
    public void WithCode_ShouldReturnNewInstance()
    {
        var error = Error.From("message");
        var reclassified = error.WithCode(ErrorCodes.Validation);

        Assert.Equal(ErrorCodes.Validation, reclassified.Code);
        Assert.Null(error.Code);
    }

    [Fact(Timeout = 15_000)]
    public void WithCause_ShouldAttachException()
    {
        var error = Error.From("message");
        var cause = new InvalidOperationException("oops");
        var enriched = error.WithCause(cause);

        Assert.Same(cause, enriched.Cause);
    }

    [Fact(Timeout = 15_000)]
    public void FromException_ShouldThrow_WhenExceptionIsNull() => Assert.Throws<ArgumentNullException>(static () => Error.FromException(null!));

    [Fact(Timeout = 15_000)]
    public void TryGetMetadata_ShouldReturnValue()
    {
        var error = Error.From("message").WithMetadata("count", 5);

        Assert.True(error.TryGetMetadata("count", out int value));
        Assert.Equal(5, value);
    }

    [Fact(Timeout = 15_000)]
    public void TryGetMetadata_ShouldReturnFalseWhenMissing()
    {
        var error = Error.From("message");

        Assert.False(error.TryGetMetadata("missing", out int _));
    }

    [Fact(Timeout = 15_000)]
    public void Timeout_ShouldIncludeDuration()
    {
        var duration = TimeSpan.FromSeconds(5);
        var error = Error.Timeout(duration);

        Assert.Equal(ErrorCodes.Timeout, error.Code);
        Assert.True(error.Metadata.TryGetValue("duration", out var value));
        Assert.Equal(duration, value);
    }

    [Fact(Timeout = 15_000)]
    public void Canceled_WithToken_ShouldCaptureMetadata()
    {
        using var cts = new CancellationTokenSource();
        var error = Error.Canceled(token: cts.Token);

        Assert.Equal(ErrorCodes.Canceled, error.Code);
        Assert.True(error.Metadata.ContainsKey("cancellationToken"));
        Assert.IsType<OperationCanceledException>(error.Cause);
    }

    [Fact(Timeout = 15_000)]
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

    [Fact(Timeout = 15_000)]
    public void From_ShouldAttachDescriptorMetadata_ForKnownCode()
    {
        var error = Error.From("invalid payload", ErrorCodes.Validation);

        Assert.Equal("Validation", error.Metadata["error.name"]);
        Assert.Equal("Input validation failed or precondition was not satisfied.", error.Metadata["error.description"]);
        Assert.Equal("General", error.Metadata["error.category"]);
    }

    [Fact(Timeout = 15_000)]
    public void ErrorCodes_ShouldExposeDescriptors()
    {
        Assert.True(ErrorCodes.TryGetDescriptor(ErrorCodes.Timeout, out var descriptor));
        Assert.Equal("Timeout", descriptor.Name);
        Assert.Equal("error.timeout", descriptor.Code);
        Assert.Equal("General", descriptor.Category);
        Assert.Equal("Operation exceeded the allotted deadline.", descriptor.Description);
    }
}
