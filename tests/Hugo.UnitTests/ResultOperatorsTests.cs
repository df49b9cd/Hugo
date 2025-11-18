using Shouldly;

namespace Hugo.Tests;

public sealed class ResultOperatorsTests
{
    [Fact(Timeout = 5_000)]
    public void Group_ShouldPropagateFailure()
    {
        var sequence = new[]
        {
            Result.Ok(1),
            Result.Fail<int>(Error.From("fail"))
        };

        var result = Result.Group(sequence, value => value % 2);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
    }

    [Fact(Timeout = 5_000)]
    public void Partition_ShouldSplitValues()
    {
        var sequence = new[]
        {
            Result.Ok(1),
            Result.Ok(2),
            Result.Ok(3)
        };

        var result = Result.Partition(sequence, v => v % 2 == 0);

        result.IsSuccess.ShouldBeTrue();
        result.Value.True.ShouldBe([2]);
        result.Value.False.ShouldBe([1, 3]);
    }

    [Fact(Timeout = 5_000)]
    public void Window_ShouldIncludeTrailingWindow()
    {
        var sequence = new[]
        {
            Result.Ok(1),
            Result.Ok(2),
            Result.Ok(3)
        };

        var result = Result.Window(sequence, size: 2);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2);
        result.Value[0].ShouldBe([1, 2]);
        result.Value[1].ShouldBe([3]);
    }
}
