using static Hugo.Go;

namespace Hugo.Tests;

public class ResultEdgeCaseTests
{
    [Fact]
    public void Ensure_ShouldAttachCustomMetadata()
    {
        var result = Ok(5)
            .Ensure(
                static value => value > 10,
                static value => Error.From("value too small", ErrorCodes.Validation)
                    .WithMetadata("actual", value)
                    .WithMetadata("threshold", 10));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.True(result.Error!.TryGetMetadata<int>("actual", out var actual));
        Assert.Equal(5, actual);
    }

    [Fact]
    public void Recover_ShouldReceiveOriginalMetadata()
    {
        var originalError = Error.From("missing", ErrorCodes.Validation)
            .WithMetadata("field", "email")
            .WithMetadata("code", "user-input");

        var recovered = Result.Fail<int>(originalError).Recover(static err =>
        {
            Assert.True(err.TryGetMetadata<string>("field", out var field));
            Assert.Equal("email", field);
            return Result.Ok(err.Message.Length);
        });

        Assert.True(recovered.IsSuccess);
        Assert.Equal("missing".Length, recovered.Value);
    }
}
