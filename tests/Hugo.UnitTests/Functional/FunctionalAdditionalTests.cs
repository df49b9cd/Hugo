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
    public void Ensure_ShouldReturnSuccess_WhenPredicateTrue()
    {
        var result = Result.Ok(5).Ensure(v => v > 0, _ => Error.From("neg", ErrorCodes.Validation));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(5);
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

    [Fact(Timeout = 15_000)]
    public void Finally_ShouldReturnSuccessProjection_WhenSuccess()
    {
        var projected = Result.Ok(3).Finally(v => v * 2, _ => -1);

        projected.ShouldBe(6);
    }
}
