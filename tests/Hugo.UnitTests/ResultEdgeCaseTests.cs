using Shouldly;

using static Hugo.Go;

namespace Hugo.Tests;

public class ResultEdgeCaseTests
{
    [Fact(Timeout = 15_000)]
    public void Ensure_ShouldAttachCustomMetadata()
    {
        var result = Ok(5)
            .Ensure(
                static value => value > 10,
                static value => Error.From("value too small", ErrorCodes.Validation)
                    .WithMetadata("actual", value)
                    .WithMetadata("threshold", 10));

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
        result.Error!.TryGetMetadata<int>("actual", out var actual).ShouldBeTrue();
        actual.ShouldBe(5);
    }

    [Fact(Timeout = 15_000)]
    public void Recover_ShouldReceiveOriginalMetadata()
    {
        var originalError = Error.From("missing", ErrorCodes.Validation)
            .WithMetadata("field", "email")
            .WithMetadata("code", "user-input");

        var recovered = Result.Fail<int>(originalError).Recover(static err =>
        {
            err.TryGetMetadata<string>("field", out var field).ShouldBeTrue();
            field.ShouldBe("email");
            return Result.Ok(err.Message.Length);
        });

        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value.ShouldBe("missing".Length);
    }
}
