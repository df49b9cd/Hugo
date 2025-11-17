using Shouldly;
namespace Hugo.Tests;

public class OptionalTests
{
    [Fact(Timeout = 15_000)]
    public void Some_ShouldContainValue()
    {
        var optional = Optional.Some(42);

        optional.HasValue.ShouldBeTrue();
        optional.HasNoValue.ShouldBeFalse();
        optional.Value.ShouldBe(42);
    }

    [Fact(Timeout = 15_000)]
    public void None_ShouldIndicateAbsence()
    {
        var optional = Optional.None<int>();

        optional.HasValue.ShouldBeFalse();
        optional.HasNoValue.ShouldBeTrue();
        optional.TryGetValue(out _).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void Value_ShouldThrow_WhenNoValue()
    {
        var optional = Optional.None<string>();

        Should.Throw<InvalidOperationException>(() => optional.Value);
    }

    [Fact(Timeout = 15_000)]
    public void ValueOr_ShouldReturnFallback_WhenNone()
    {
        var optional = Optional.None<string>();

        optional.ValueOr("fallback").ShouldBe("fallback");
    }

    [Fact(Timeout = 15_000)]
    public void ValueOr_ShouldInvokeFactory_WhenNone()
    {
        var optional = Optional.None<string>();
        var invoked = false;

        var value = optional.ValueOr(() =>
        {
            invoked = true;
            return "generated";
        });

        invoked.ShouldBeTrue();
        value.ShouldBe("generated");
    }

    [Fact(Timeout = 15_000)]
    public void Match_ShouldSelectCorrectBranch()
    {
        var some = Optional.Some("value");
        var none = Optional.None<string>();

        some.Match(static v => v, static () => "none").ShouldBe("value");
        none.Match(static v => v, static () => "none").ShouldBe("none");
    }

    [Fact(Timeout = 15_000)]
    public void Switch_ShouldInvokeCorrectBranch()
    {
        var valueBranchInvoked = false;
        var noneBranchInvoked = false;

        Optional.Some(5).Switch(_ => valueBranchInvoked = true, () => noneBranchInvoked = true);
        Optional.None<int>().Switch(_ => valueBranchInvoked = true, () => noneBranchInvoked = true);

        valueBranchInvoked.ShouldBeTrue();
        noneBranchInvoked.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public void Map_ShouldTransformValue()
    {
        var mapped = Optional.Some(5).Map(static n => n * 2);

        mapped.HasValue.ShouldBeTrue();
        mapped.Value.ShouldBe(10);
    }

    [Fact(Timeout = 15_000)]
    public void Bind_ShouldFlattenOptionals()
    {
        var bound = Optional.Some(2).Bind(static n => Optional.Some(n * 3));

        bound.HasValue.ShouldBeTrue();
        bound.Value.ShouldBe(6);
    }

    [Fact(Timeout = 15_000)]
    public void Filter_ShouldDiscardValue_WhenPredicateFails()
    {
        var filtered = Optional.Some(4).Filter(static n => n % 2 == 1);

        filtered.HasValue.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void Or_ShouldReturnAlternative_WhenNone()
    {
        var alternative = Optional.Some("alt");
        var value = Optional.None<string>().Or(alternative);

        value.HasValue.ShouldBeTrue();
        value.Value.ShouldBe("alt");
    }

    [Fact(Timeout = 15_000)]
    public void ToResult_ShouldReturnSuccess_WhenValuePresent()
    {
        var optional = Optional.Some("value");

        var result = optional.ToResult(static () => Error.From("missing", ErrorCodes.Validation));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("value");
    }

    [Fact(Timeout = 15_000)]
    public void ToResult_ShouldUseErrorFactory_WhenNone()
    {
        var optional = Optional.None<int>();

        var result = optional.ToResult(static () => Error.From("missing", ErrorCodes.Validation));

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public void ResultToOptional_ShouldReturnValue_WhenSuccess()
    {
        var result = Result.Ok("value");

        var optional = result.ToOptional();

        optional.HasValue.ShouldBeTrue();
        optional.Value.ShouldBe("value");
    }

    [Fact(Timeout = 15_000)]
    public void ResultToOptional_ShouldReturnNone_WhenFailure()
    {
        var result = Result.Fail<string>(Error.From("missing"));

        var optional = result.ToOptional();

        optional.HasValue.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void FromOptional_ShouldReturnSuccess_WhenValuePresent()
    {
        var optional = Optional.Some(10);

        var result = Result.FromOptional(optional, static () => Error.From("missing"));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(10);
    }

    [Fact(Timeout = 15_000)]
    public void FromOptional_ShouldReturnFailure_WhenNone()
    {
        var optional = Optional.None<int>();

        var result = Result.FromOptional(optional, static () => Error.From("missing", ErrorCodes.Validation));

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public void FromNullable_ShouldReturnNone_WhenReferenceIsNull()
    {
        var optional = Optional.FromNullable<string>(null);

        optional.HasValue.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void FromNullable_ShouldReturnSome_WhenReferenceHasValue()
    {
        var optional = Optional.FromNullable("value");

        optional.HasValue.ShouldBeTrue();
        optional.Value.ShouldBe("value");
    }

    [Fact(Timeout = 15_000)]
    public void FromNullable_ValueType_ShouldReturnSome_WhenHasValue()
    {
        int? input = 5;

        var optional = Optional.FromNullable(input);

        optional.HasValue.ShouldBeTrue();
        optional.Value.ShouldBe(5);
    }

    [Fact(Timeout = 15_000)]
    public void FromNullable_ValueType_ShouldReturnNone_WhenNoValue()
    {
        int? input = null;

        var optional = Optional.FromNullable(input);

        optional.HasValue.ShouldBeFalse();
    }
}
