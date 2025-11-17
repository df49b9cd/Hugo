using Shouldly;

namespace Hugo.Tests.Functional;

public sealed class FunctionalAdditionalTests
{
    [Fact(Timeout = 15_000)]
    public void Ensure_ShouldReturnFailure_WhenPredicateFalse()
    {
        var result = Result.Ok(1).Ensure(_ => false, _ => Error.From("fail", ErrorCodes.Validation));

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public void Finally_ShouldExecuteRegardlessOfOutcome()
    {
        bool called = false;

        var result = Result.Fail<int>(Error.From("boom", ErrorCodes.Exception))
            .Finally(
                _ =>
                {
                    called = true;
                    return 0;
                },
                _ =>
                {
                    called = true;
                    return 0;
                });

        called.ShouldBeTrue();
        result.ShouldBe(0);
    }
}
