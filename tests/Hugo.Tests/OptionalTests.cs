namespace Hugo.Tests;

public class OptionalTests
{
    [Fact]
    public void Some_ShouldContainValue()
    {
        var optional = Optional.Some(42);

        Assert.True(optional.HasValue);
        Assert.False(optional.HasNoValue);
        Assert.Equal(42, optional.Value);
    }

    [Fact]
    public void None_ShouldIndicateAbsence()
    {
        var optional = Optional<int>.None();

        Assert.False(optional.HasValue);
        Assert.True(optional.HasNoValue);
        Assert.False(optional.TryGetValue(out _));
    }

    [Fact]
    public void Value_ShouldThrow_WhenNoValue()
    {
        var optional = Optional<string>.None();

        Assert.Throws<InvalidOperationException>(() => optional.Value);
    }

    [Fact]
    public void ValueOr_ShouldReturnFallback_WhenNone()
    {
        var optional = Optional<string>.None();

        Assert.Equal("fallback", optional.ValueOr("fallback"));
    }

    [Fact]
    public void ValueOr_ShouldInvokeFactory_WhenNone()
    {
        var optional = Optional<string>.None();
        var invoked = false;

        var value = optional.ValueOr(() =>
        {
            invoked = true;
            return "generated";
        });

        Assert.True(invoked);
        Assert.Equal("generated", value);
    }

    [Fact]
    public void Match_ShouldSelectCorrectBranch()
    {
        var some = Optional.Some("value");
        var none = Optional<string>.None();

        Assert.Equal("value", some.Match(static v => v, static () => "none"));
        Assert.Equal("none", none.Match(static v => v, static () => "none"));
    }

    [Fact]
    public void Switch_ShouldInvokeCorrectBranch()
    {
        var valueBranchInvoked = false;
        var noneBranchInvoked = false;

        Optional.Some(5).Switch(_ => valueBranchInvoked = true, () => noneBranchInvoked = true);
        Optional<int>.None().Switch(_ => valueBranchInvoked = true, () => noneBranchInvoked = true);

        Assert.True(valueBranchInvoked);
        Assert.True(noneBranchInvoked);
    }

    [Fact]
    public void Map_ShouldTransformValue()
    {
        var mapped = Optional.Some(5).Map(static n => n * 2);

        Assert.True(mapped.HasValue);
        Assert.Equal(10, mapped.Value);
    }

    [Fact]
    public void Bind_ShouldFlattenOptionals()
    {
        var bound = Optional.Some(2).Bind(static n => Optional.Some(n * 3));

        Assert.True(bound.HasValue);
        Assert.Equal(6, bound.Value);
    }

    [Fact]
    public void Filter_ShouldDiscardValue_WhenPredicateFails()
    {
        var filtered = Optional.Some(4).Filter(static n => n % 2 == 1);

        Assert.False(filtered.HasValue);
    }

    [Fact]
    public void Or_ShouldReturnAlternative_WhenNone()
    {
        var alternative = Optional.Some("alt");
        var value = Optional<string>.None().Or(alternative);

        Assert.True(value.HasValue);
        Assert.Equal("alt", value.Value);
    }

    [Fact]
    public void ToResult_ShouldReturnSuccess_WhenValuePresent()
    {
        var optional = Optional.Some("value");

        var result = optional.ToResult(static () => Error.From("missing", ErrorCodes.Validation));

        Assert.True(result.IsSuccess);
        Assert.Equal("value", result.Value);
    }

    [Fact]
    public void ToResult_ShouldUseErrorFactory_WhenNone()
    {
        var optional = Optional<int>.None();

        var result = optional.ToResult(static () => Error.From("missing", ErrorCodes.Validation));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public void ResultToOptional_ShouldReturnValue_WhenSuccess()
    {
        var result = Result.Ok("value");

        var optional = result.ToOptional();

        Assert.True(optional.HasValue);
        Assert.Equal("value", optional.Value);
    }

    [Fact]
    public void ResultToOptional_ShouldReturnNone_WhenFailure()
    {
        var result = Result.Fail<string>(Error.From("missing"));

        var optional = result.ToOptional();

        Assert.False(optional.HasValue);
    }

    [Fact]
    public void FromOptional_ShouldReturnSuccess_WhenValuePresent()
    {
        var optional = Optional.Some(10);

        var result = Result.FromOptional(optional, static () => Error.From("missing"));

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value);
    }

    [Fact]
    public void FromOptional_ShouldReturnFailure_WhenNone()
    {
        var optional = Optional<int>.None();

        var result = Result.FromOptional(optional, static () => Error.From("missing", ErrorCodes.Validation));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public void FromNullable_ShouldReturnNone_WhenReferenceIsNull()
    {
        var optional = Optional.FromNullable<string>(null);

        Assert.False(optional.HasValue);
    }

    [Fact]
    public void FromNullable_ShouldReturnSome_WhenReferenceHasValue()
    {
        var optional = Optional.FromNullable("value");

        Assert.True(optional.HasValue);
        Assert.Equal("value", optional.Value);
    }

    [Fact]
    public void FromNullable_ValueType_ShouldReturnSome_WhenHasValue()
    {
        int? input = 5;

        var optional = Optional.FromNullable(input);

        Assert.True(optional.HasValue);
        Assert.Equal(5, optional.Value);
    }

    [Fact]
    public void FromNullable_ValueType_ShouldReturnNone_WhenNoValue()
    {
        int? input = null;

        var optional = Optional.FromNullable(input);

        Assert.False(optional.HasValue);
    }
}
