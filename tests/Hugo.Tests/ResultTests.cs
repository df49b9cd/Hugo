using System.Runtime.CompilerServices;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

internal class ResultTests
{
    [Fact]
    public void Fail_WithNullError_ShouldReturnUnspecified()
    {
        var result = Result.Fail<int>(null!);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Unspecified, result.Error?.Code);
    }

    [Fact]
    public void Try_ShouldReturnOperationValue()
    {
        var result = Result.Try(static () => 42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Try_ShouldCaptureExceptionWhenOperationFails()
    {
        var exception = new InvalidOperationException("boom");

        var result = Result.Try<int>(() => throw exception);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Same(exception, result.Error?.Cause);
    }

    [Fact]
    public void Try_ShouldUseErrorFactory()
    {
        var custom = Error.From("custom", ErrorCodes.Validation);

        var result = Result.Try<int>(() => throw new InvalidOperationException("fail"), _ => custom);

        Assert.True(result.IsFailure);
        Assert.Same(custom, result.Error);
    }

    [Fact]
    public void Try_ShouldFallbackToExceptionWhenFactoryReturnsNull()
    {
        var result = Result.Try<int>(static () => throw new InvalidOperationException("fail"), static _ => null);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
    }

    [Fact]
    public async Task TryAsync_ShouldReturnOperationValue()
    {
        var result = await Result.TryAsync(static _ => Task.FromResult(21), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(21, result.Value);
    }

    [Fact]
    public async Task TryAsync_ShouldCaptureExceptionWhenOperationFails()
    {
        var exception = new InvalidOperationException("boom");

        var result = await Result.TryAsync(_ => Task.FromException<int>(exception), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Same(exception, result.Error?.Cause);
    }

    [Fact]
    public async Task TryAsync_ShouldRespectErrorFactory()
    {
        var custom = Error.From("async", ErrorCodes.Validation);

        var result = await Result.TryAsync(
            _ => Task.FromException<int>(new InvalidOperationException("fail")),
            TestContext.Current.CancellationToken,
            _ => custom);

        Assert.True(result.IsFailure);
        Assert.Same(custom, result.Error);
    }

    [Fact]
    public async Task TryAsync_ShouldReturnCanceledResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Result.TryAsync(
            static async token =>
            {
                await Task.Delay(10, token);
                return 1;
            },
            cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.True(result.Error!.TryGetMetadata("cancellationToken", out CancellationToken recorded));
        Assert.Equal(cts.Token, recorded);
    }

    [Fact]
    public async Task TryAsync_ShouldFallbackToExceptionWhenFactoryReturnsNull()
    {
        var result = await Result.TryAsync(
            static _ => Task.FromException<int>(new InvalidOperationException("fail")),
            TestContext.Current.CancellationToken,
            static _ => null);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
    }

    [Fact]
    public void FromOptional_ShouldReturnSuccessWhenValuePresent()
    {
        var optional = Optional<int>.Some(5);

        var result = Result.FromOptional(optional, static () => Error.From("missing"));

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public void FromOptional_ShouldInvokeErrorFactoryWhenValueMissing()
    {
        var optional = Optional<int>.None();
        var custom = Error.From("missing", ErrorCodes.Validation);

        var result = Result.FromOptional(optional, () => custom);

        Assert.True(result.IsFailure);
        Assert.Same(custom, result.Error);
    }

    [Fact]
    public void FromOptional_ShouldFallbackToUnspecifiedWhenFactoryReturnsNull()
    {
        var optional = Optional<int>.None();

        var result = Result.FromOptional(optional, static () => null!);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Unspecified, result.Error?.Code);
    }

    [Fact]
    public void FromOptional_ShouldThrowWhenFactoryIsNull() => Assert.Throws<ArgumentNullException>(static () => Result.FromOptional(Optional<int>.Some(1), null!));

    [Fact]
    public void Traverse_ShouldThrow_WhenSourceIsNull() => Assert.Throws<ArgumentNullException>(static () => Result.Traverse<int, int>(null!, static x => Result.Ok(x)));

    [Fact]
    public void Traverse_ShouldThrow_WhenSelectorIsNull() => Assert.Throws<ArgumentNullException>(static () => Result.Traverse(Array.Empty<int>(), (Func<int, Result<int>>)null!));

    [Fact]
    public void Sequence_ShouldReturnFirstFailure()
    {
        var error = Error.From("fail");
        var result = Result.Sequence([Result.Ok(1), Result.Fail<int>(error), Result.Ok(3)]);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void Sequence_ShouldAggregateSuccessfulValues()
    {
        var result = Result.Sequence([Result.Ok(1), Result.Ok(2), Result.Ok(3)]);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2, 3], result.Value);
    }

    [Fact]
    public void Traverse_ShouldApplySelector()
    {
        var result = Result.Traverse([1, 2, 3], static n => Result.Ok(n * 2));

        Assert.True(result.IsSuccess);
        Assert.Equal([2, 4, 6], result.Value);
    }

    [Fact]
    public void Traverse_ShouldStopOnFailure()
    {
        var error = Error.From("fail");
        var result = Result.Traverse([1, 2, 3], n => n == 2 ? Result.Fail<int>(error) : Result.Ok(n));

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task TraverseAsync_ShouldAggregateSuccessfulValues()
    {
        var result = await Result.TraverseAsync([1, 2], static n => Task.FromResult(Result.Ok(n + 1)), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal([2, 3], result.Value);
    }

    [Fact]
    public async Task TraverseAsync_ShouldThrow_WhenSourceIsNull() => await Assert.ThrowsAsync<ArgumentNullException>(static () => Result.TraverseAsync((IEnumerable<int>)null!, static _ => Task.FromResult(Result.Ok(0)), TestContext.Current.CancellationToken));

    [Fact]
    public async Task TraverseAsync_ShouldThrow_WhenSelectorIsNull() => await Assert.ThrowsAsync<ArgumentNullException>(static () => Result.TraverseAsync(Array.Empty<int>(), (Func<int, Task<Result<int>>>)null!, TestContext.Current.CancellationToken));

    [Fact]
    public async Task TraverseAsync_ShouldReturnFailure()
    {
        var error = Error.From("fail");
        var result = await Result.TraverseAsync([1, 2], n => Task.FromResult(n == 2 ? Result.Fail<int>(error) : Result.Ok(n)), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task TraverseAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Result.TraverseAsync([1], static _ => Task.FromResult(Result.Ok(1)), cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.True(result.Error!.TryGetMetadata("cancellationToken", out CancellationToken recordedToken));
        Assert.Equal(cts.Token, recordedToken);
    }

    [Fact]
    public async Task TraverseAsync_WithTokenAwareSelector_ShouldPassCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var observedToken = CancellationToken.None;
        var observed = false;

        var result = await Result.TraverseAsync(
            [1, 2],
            (value, token) =>
            {
                observedToken = token;
                observed = true;
                return Task.FromResult(Result.Ok(value));
            },
            cts.Token);

        Assert.True(result.IsSuccess);
        Assert.True(observed);
        Assert.Equal(cts.Token, observedToken);
    }

    [Fact]
    public async Task TraverseAsync_WithTokenAwareSelector_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Result.TraverseAsync(
            [1],
            static (value, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Result.Ok(value));
            },
            cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.True(result.Error!.TryGetMetadata("cancellationToken", out CancellationToken recordedToken));
        Assert.Equal(cts.Token, recordedToken);
    }

    [Fact]
    public async Task SequenceAsync_Stream_ShouldAggregateValues()
    {
        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return Result.Ok(1);
            await Task.Yield();
            yield return Result.Ok(2);
        }

        var result = await Result.SequenceAsync(Source(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2], result.Value);
    }

    [Fact]
    public async Task SequenceAsync_Stream_ShouldStopAtFailure()
    {
        var error = Error.From("fail");
        var enumeratedAfterFailure = false;

        static async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct = default, Error? error = null, bool enumeratedAfterFailure = false)
        {
            yield return Result.Ok(1);
            yield return Result.Fail<int>(error);
            enumeratedAfterFailure = true;
            yield return Result.Ok(3);
        }

        var result = await Result.SequenceAsync(Source(TestContext.Current.CancellationToken, error, enumeratedAfterFailure), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.False(enumeratedAfterFailure);
    }

    [Fact]
    public async Task SequenceAsync_Stream_ShouldReturnCanceledError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(50, ct);
            yield return Result.Ok(1);
        }

        var result = await Result.SequenceAsync(Source(cts.Token), cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task SequenceAsync_Stream_WithFakeTimeProvider_ShouldReturnCanceledError()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        using var timer = provider.CreateTimer(_ => cts.Cancel(), state: null, dueTime: TimeSpan.FromSeconds(1), period: Timeout.InfiniteTimeSpan);

        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            yield return Result.Ok(1);
        }

        var resultTask = Result.SequenceAsync(Source(cts.Token), cts.Token);

        provider.Advance(TimeSpan.FromSeconds(1));

        var result = await resultTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task TraverseAsync_Stream_ShouldAggregateValues()
    {
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            await Task.Yield();
            yield return 2;
        }

        var result = await Result.TraverseAsync(
            Source(TestContext.Current.CancellationToken),
            static (value, _) => new ValueTask<Result<int>>(Result.Ok(value * 2)),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal([2, 4], result.Value);
    }

    [Fact]
    public async Task TraverseAsync_Stream_ShouldStopOnFailure()
    {
        var error = Error.From("fail");
        var enumeratedAfterFailure = false;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            yield return 2;
            enumeratedAfterFailure = true;
            yield return 3;
        }

        var result = await Result.TraverseAsync(
            Source(TestContext.Current.CancellationToken),
            (value, _) => value == 2
                ? new ValueTask<Result<int>>(Result.Fail<int>(error))
                : new ValueTask<Result<int>>(Result.Ok(value)),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.False(enumeratedAfterFailure);
    }

    [Fact]
    public async Task TraverseAsync_Stream_ShouldReturnCanceledError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(50, ct);
            yield return 1;
        }

        var result = await Result.TraverseAsync(
            Source(cts.Token),
            static (value, _) => new ValueTask<Result<int>>(Result.Ok(value)),
            cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task MapStreamAsync_ShouldMapValues()
    {
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            await Task.Yield();
            yield return 2;
        }

        var collected = new List<Result<int>>();

        Func<int, CancellationToken, ValueTask<Result<int>>> selector = static (value, _) => new ValueTask<Result<int>>(Result.Ok(value * 3));

        await foreach (var result in Result.MapStreamAsync(
                   Source(TestContext.Current.CancellationToken),
                   selector,
                   TestContext.Current.CancellationToken))
        {
            collected.Add(result);
        }

        Assert.Collection(
            collected,
            static r =>
            {
                Assert.True(r.IsSuccess);
                Assert.Equal(3, r.Value);
            },
            static r =>
            {
                Assert.True(r.IsSuccess);
                Assert.Equal(6, r.Value);
            });
    }

    [Fact]
    public async Task MapStreamAsync_ShouldStopOnFailure()
    {
        var error = Error.From("fail");
        var enumeratedAfterFailure = false;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            yield return 2;
            enumeratedAfterFailure = true;
            yield return 3;
        }

        var collected = new List<Result<int>>();

        Func<int, CancellationToken, ValueTask<Result<int>>> selector = (value, _) => value == 2
            ? new ValueTask<Result<int>>(Result.Fail<int>(error))
            : new ValueTask<Result<int>>(Result.Ok(value));

        await foreach (var result in Result.MapStreamAsync(
                       Source(TestContext.Current.CancellationToken),
                       selector,
                       TestContext.Current.CancellationToken))
        {
            collected.Add(result);
        }

        Assert.Equal(2, collected.Count);
        Assert.True(collected[0].IsSuccess);
        Assert.True(collected[1].IsFailure);
        Assert.Same(error, collected[1].Error);
        Assert.False(enumeratedAfterFailure);
    }

    [Fact]
    public async Task MapStreamAsync_TaskSelector_ShouldMapValues()
    {
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
        }

        var collected = new List<Result<int>>();

        Func<int, CancellationToken, Task<Result<int>>> selector = static async (value, ct) =>
        {
            await Task.Delay(10, ct);
            return Result.Ok(value + 1);
        };
        await foreach (var result in Result.MapStreamAsync(
                       Source(TestContext.Current.CancellationToken),
                       selector,
                       TestContext.Current.CancellationToken))
        {
            collected.Add(result);
        }

        Assert.Single(collected);
        Assert.True(collected[0].IsSuccess);
        Assert.Equal(2, collected[0].Value);
    }

    [Fact]
    public void Match_ShouldInvokeSuccessBranch()
    {
        var result = Result.Ok(42);

        var observed = result.Match(static value => value + 1, static _ => -1);

        Assert.Equal(43, observed);
    }

    [Fact]
    public void Match_ShouldInvokeFailureBranch()
    {
        var error = Error.From("boom");
        var result = Result.Fail<int>(error);

        var observed = result.Match(static _ => -1, static err => err.Message.Length);

        Assert.Equal(error.Message.Length, observed);
    }

    [Fact]
    public void Switch_ShouldInvokeCorrectCallback()
    {
        var success = Result.Ok("value");
        var failure = Result.Fail<string>(Error.From("fail"));

        var successCalled = false;
        var failureCalled = false;

        success.Switch(_ => successCalled = true, _ => failureCalled = true);

        Assert.True(successCalled);
        Assert.False(failureCalled);

        successCalled = false;
        failureCalled = false;

        failure.Switch(_ => successCalled = true, _ => failureCalled = true);

        Assert.False(successCalled);
        Assert.True(failureCalled);
    }

    [Fact]
    public async Task MatchAsync_ShouldRespectOutcome()
    {
        var success = Result.Ok(5);
        var failure = Result.Fail<int>(Error.From("fail"));

        var successValue = await success.MatchAsync(
            static (value, token) => ValueTask.FromResult(value * 2),
            static (_, token) => ValueTask.FromResult(-1),
            TestContext.Current.CancellationToken);

        Assert.Equal(10, successValue);

        var failureValue = await failure.MatchAsync(
            static (_, token) => ValueTask.FromResult(-1),
            static (error, token) => ValueTask.FromResult(error.Message.Length),
            TestContext.Current.CancellationToken);

        Assert.Equal(failure.Error!.Message.Length, failureValue);
    }

    [Fact]
    public void TryGetValueAndError_ShouldExposeState()
    {
        var success = Result.Ok(10);
        Assert.True(success.TryGetValue(out var value));
        Assert.Equal(10, value);
        Assert.False(success.TryGetError(out _));

        var error = Error.From("oops");
        var failure = Result.Fail<int>(error);
        Assert.False(failure.TryGetValue(out _));
        Assert.True(failure.TryGetError(out var observedError));
        Assert.Same(error, observedError);
    }

    [Fact]
    public async Task MapStreamAsync_ShouldReturnCanceledResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(50, ct);
            yield return 1;
        }

        var collected = new List<Result<int>>();

        Func<int, CancellationToken, ValueTask<Result<int>>> selector = static (value, _) => new ValueTask<Result<int>>(Result.Ok(value));

        await foreach (var result in Result.MapStreamAsync(
                   Source(cts.Token),
                   selector,
                   cts.Token))
        {
            collected.Add(result);
        }

        Assert.Single(collected);
        Assert.True(collected[0].IsFailure);
        Assert.Equal(ErrorCodes.Canceled, collected[0].Error?.Code);
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
        var value = Result.Fail<int>(Error.From("err")).ValueOr(static error => error.Message.Length);

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
    public void ToOptional_ShouldReflectResultState()
    {
        var success = Result.Ok(7).ToOptional();
        Assert.True(success.TryGetValue(out var value));
        Assert.Equal(7, value);

        var failure = Result.Fail<int>(Error.From("boom")).ToOptional();
        Assert.False(failure.TryGetValue(out _));
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
