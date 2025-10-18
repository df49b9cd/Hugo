using System.Threading;
using System.Threading.Tasks;
using Hugo;

namespace Hugo.Tests;

public class ResultTests
{
    [Fact]
    public void Fail_WithNullError_ShouldReturnUnspecified()
    {
        var result = Result.Fail<int>(null!);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Unspecified, result.Error?.Code);
    }

    [Fact]
    public void Try_ShouldReturnSuccess()
    {
        var result = Result.Try(() => 5);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public void Try_ShouldUseCustomErrorFactory()
    {
        var customError = Error.From("custom", ErrorCodes.Validation);
        var result = Result.Try<int>(() => throw new InvalidOperationException("boom"), _ => customError);

        Assert.True(result.IsFailure);
        Assert.Same(customError, result.Error);
    }

    [Fact]
    public void Try_ShouldPropagateOperationCanceledException()
    {
        Assert.Throws<OperationCanceledException>(() => Result.Try<int>(() => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task TryAsync_ShouldReturnSuccess()
    {
        var result = await Result.TryAsync(async ct =>
        {
            await Task.Delay(10, ct);
            return 7;
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public async Task TryAsync_ShouldUseCustomErrorFactory()
    {
        var customError = Error.From("async", ErrorCodes.Validation);
        var result = await Result.TryAsync<int>(
            _ => throw new InvalidOperationException("fail"),
            TestContext.Current.CancellationToken,
            _ => customError
        );

        Assert.True(result.IsFailure);
        Assert.Same(customError, result.Error);
    }

    [Fact]
    public async Task TryAsync_ShouldReturnCanceledError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Result.TryAsync<int>(async ct =>
        {
            await Task.Delay(50, ct);
            return 42;
        }, cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public void Sequence_ShouldAggregateSuccessfulValues()
    {
        var result = Result.Sequence(new[] { Result.Ok(1), Result.Ok(2), Result.Ok(3) });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 1, 2, 3 }, result.Value);
    }

    [Fact]
    public void Sequence_ShouldReturnFirstFailure()
    {
        var error = Error.From("fail");
        var result = Result.Sequence(new[] { Result.Ok(1), Result.Fail<int>(error), Result.Ok(3) });

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void Traverse_ShouldApplySelector()
    {
        var result = Result.Traverse(new[] { 1, 2, 3 }, n => Result.Ok(n * 2));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 2, 4, 6 }, result.Value);
    }

    [Fact]
    public void Traverse_ShouldStopOnFailure()
    {
        var error = Error.From("fail");
        var result = Result.Traverse(new[] { 1, 2, 3 }, n => n == 2 ? Result.Fail<int>(error) : Result.Ok(n));

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task TraverseAsync_ShouldAggregateSuccessfulValues()
    {
    var result = await Result.TraverseAsync(new[] { 1, 2 }, n => Task.FromResult(Result.Ok(n + 1)), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 2, 3 }, result.Value);
    }

    [Fact]
    public async Task TraverseAsync_ShouldReturnFailure()
    {
        var error = Error.From("fail");
    var result = await Result.TraverseAsync(new[] { 1, 2 }, n => Task.FromResult(n == 2 ? Result.Fail<int>(error) : Result.Ok(n)), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task TraverseAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Result.TraverseAsync(new[] { 1 }, _ => Task.FromResult(Result.Ok(1)), cts.Token));
    }

    [Fact]
    public void ValueOr_ShouldReturnFallbackWhenFailure()
    {
        var value = Result.Fail<int>(Error.From("err")).ValueOr(42);

        Assert.Equal(42, value);
    }

    [Fact]
    public void ValueOrFactory_ShouldInvokeWhenFailure()
    {
        var value = Result.Fail<int>(Error.From("err")).ValueOr(error => error.Message.Length);

        Assert.Equal(3, value);
    }

    [Fact]
    public void ValueOrThrow_ShouldThrowOnFailure()
    {
        var result = Result.Fail<int>(Error.From("boom"));

        _ = Assert.Throws<ResultException>(() => result.ValueOrThrow());
    }

    [Fact]
    public void Deconstruct_ShouldExposeValueAndError()
    {
        var success = Result.Ok(5);
        var failure = Result.Fail<int>(Error.From("fail"));

        var (value, error) = success;
        Assert.Equal(5, value);
        Assert.Null(error);

        var (_, failureError) = failure;
        Assert.NotNull(failureError);
    }

    [Fact]
    public void ImplicitTupleConversion_ShouldRoundTrip()
    {
        Result<int> fromTuple = (10, null);
        var roundTrip = Result.Ok(20);
        (int value, Error? error) = roundTrip;

        Assert.True(fromTuple.IsSuccess);
        Assert.Equal(10, fromTuple.Value);
        Assert.True(roundTrip.IsSuccess);
        Assert.Equal(20, value);
        Assert.Null(error);
    }

    [Fact]
    public void ToString_ShouldIndicateState()
    {
        var success = Result.Ok(3).ToString();
        var failure = Result.Fail<int>(Error.From("fail")).ToString();

        Assert.Equal("Ok(3)", success);
        Assert.StartsWith("Err(", failure);
    }
}
