using Shouldly;
namespace Hugo.Tests;

public class ErrorTests
{
    [Fact(Timeout = 15_000)]
    public void WithMetadata_ShouldAddEntry()
    {
        var error = Error.From("boom", ErrorCodes.Exception);
        var enriched = error.WithMetadata("key", 123);

        enriched.Message.ShouldBe(error.Message);
        enriched.Metadata.ContainsKey("key").ShouldBeTrue();
        enriched.Metadata["key"].ShouldBe(123);
    }

    [Fact(Timeout = 15_000)]
    public void WithMetadata_ShouldThrow_WhenKeyIsInvalid()
    {
        var error = Error.From("message");

        Should.Throw<ArgumentException>(() => error.WithMetadata(" ", 1));
    }

    [Fact(Timeout = 15_000)]
    public void WithMetadataCollection_ShouldMergeCaseInsensitive()
    {
        var error = Error.From("boom").WithMetadata("Key", 1);
        var enriched = error.WithMetadata([new KeyValuePair<string, object?>("key", 2)]);

        enriched.Metadata["key"].ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public void WithMetadataCollection_ShouldThrow_WhenMetadataIsNull()
    {
        var error = Error.From("message");

        Should.Throw<ArgumentNullException>(() => error.WithMetadata((IEnumerable<KeyValuePair<string, object?>>)null!));
    }

    [Fact(Timeout = 15_000)]
    public void WithCode_ShouldReturnNewInstance()
    {
        var error = Error.From("message");
        var reclassified = error.WithCode(ErrorCodes.Validation);

        reclassified.Code.ShouldBe(ErrorCodes.Validation);
        error.Code.ShouldBeNull();
    }

    [Fact(Timeout = 15_000)]
    public void WithCause_ShouldAttachException()
    {
        var error = Error.From("message");
        var cause = new InvalidOperationException("oops");
        var enriched = error.WithCause(cause);

        enriched.Cause.ShouldBeSameAs(cause);
    }

    [Fact(Timeout = 15_000)]
    public void FromException_ShouldThrow_WhenExceptionIsNull() => Should.Throw<ArgumentNullException>(static () => Error.FromException(null!));

    [Fact(Timeout = 15_000)]
    public void TryGetMetadata_ShouldReturnValue()
    {
        var error = Error.From("message").WithMetadata("count", 5);

        error.TryGetMetadata("count", out int value).ShouldBeTrue();
        value.ShouldBe(5);
    }

    [Fact(Timeout = 15_000)]
    public void TryGetMetadata_ShouldReturnFalseWhenMissing()
    {
        var error = Error.From("message");

        error.TryGetMetadata("missing", out int _).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void Timeout_ShouldIncludeDuration()
    {
        var duration = TimeSpan.FromSeconds(5);
        var error = Error.Timeout(duration);

        error.Code.ShouldBe(ErrorCodes.Timeout);
        error.Metadata.TryGetValue("duration", out var value).ShouldBeTrue();
        value.ShouldBe(duration);
    }

    [Fact(Timeout = 15_000)]
    public void Canceled_WithToken_ShouldCaptureMetadata()
    {
        using var cts = new CancellationTokenSource();
        var error = Error.Canceled(token: cts.Token);

        error.Code.ShouldBe(ErrorCodes.Canceled);
        error.Metadata.ContainsKey("cancellationToken").ShouldBeTrue();
        error.Cause.ShouldBeOfType<OperationCanceledException>();
    }

    [Fact(Timeout = 15_000)]
    public void Aggregate_ShouldIncludeChildErrors()
    {
        var first = Error.From("one");
        var second = Error.From("two");
        var aggregate = Error.Aggregate("many", first, second);

        aggregate.Code.ShouldBe(ErrorCodes.Aggregate);
        aggregate.Metadata.TryGetValue("errors", out var value).ShouldBeTrue();
        ((Error[])value!).ShouldContain(first);
        ((Error[])value!).ShouldContain(second);
    }

    [Fact(Timeout = 15_000)]
    public void From_ShouldAttachDescriptorMetadata_ForKnownCode()
    {
        var error = Error.From("invalid payload", ErrorCodes.Validation);

        error.Metadata["error.name"].ShouldBe("Validation");
        error.Metadata["error.description"].ShouldBe("Input validation failed or precondition was not satisfied.");
        error.Metadata["error.category"].ShouldBe("General");
    }

    [Fact(Timeout = 15_000)]
    public void ErrorCodes_ShouldExposeDescriptors()
    {
        ErrorCodes.TryGetDescriptor(ErrorCodes.Timeout, out var descriptor).ShouldBeTrue();
        descriptor.Name.ShouldBe("Timeout");
        descriptor.Code.ShouldBe("error.timeout");
        descriptor.Category.ShouldBe("General");
        descriptor.Description.ShouldBe("Operation exceeded the allotted deadline.");
    }
}
