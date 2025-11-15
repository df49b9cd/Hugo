using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public partial class GoTests
{
    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_Delegates_ShouldReturnAllResults()
    {
        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                return Ok(1);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
                return Ok(2);
            }
        };

        var result = await FanOutAsync<int>(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2], result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ValueTaskDelegates_ShouldReturnAllResults()
    {
        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                return Ok(10);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
                return Ok(20);
            }
        };

        var result = await FanOutValueTaskAsync<int>(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal([10, 20], result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldReturnFirstSuccessfulResult()
    {
        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
                return Err<int>("slow failure", ErrorCodes.Validation);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                return Ok(2);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(30), ct);
                return Err<int>("failure", ErrorCodes.Validation);
            }
        };

        var result = await RaceAsync<int>(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ValueTaskDelegates_ShouldReturnFirstSuccessfulResult()
    {
        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
                return Err<int>("slow failure", ErrorCodes.Validation);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
                return Ok(7);
            }
        };

        var result = await RaceValueTaskAsync<int>(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnTimeoutError()
    {
        var provider = new FakeTimeProvider();

        var timeoutTask = WithTimeoutAsync(
            static async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(42);
            },
            TimeSpan.FromSeconds(1),
            provider,
            TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(1));

        var result = await timeoutTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnCanceled_WhenTokenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeTimeProvider();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var timeoutTask = WithTimeoutAsync(
            async ct =>
            {
                operationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(42);
            },
            TimeSpan.FromSeconds(5),
            timeProvider: provider,
            cancellationToken: cts.Token);

        await operationStarted.Task;
        await cts.CancelAsync();

        var result = await timeoutTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnFailure_WhenOperationThrows()
    {
        var provider = new FakeTimeProvider();

        var result = await WithTimeoutAsync<int>(
            static _ => throw new InvalidOperationException("boom"),
            TimeSpan.FromSeconds(5),
            timeProvider: provider,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains("boom", result.Error?.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnFailure_WhenOperationThrows_WithInfiniteTimeout()
    {
        var result = await WithTimeoutAsync<int>(
            static _ => throw new InvalidOperationException("boom infinite"),
            Timeout.InfiniteTimeSpan,
            timeProvider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains("boom infinite", result.Error?.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ValueTaskDelegate_ShouldReturnSuccess()
    {
        var provider = new FakeTimeProvider();

        var result = await WithTimeoutValueTaskAsync(
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                return Ok(5);
            },
            TimeSpan.FromSeconds(1),
            provider,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldRetryUntilSuccess()
    {
        var attempts = new List<int>();

        var result = await RetryValueTaskAsync(
            async (attempt, ct) =>
            {
                attempts.Add(attempt);

                if (attempt < 3)
                {
                    return Err<int>("retry", ErrorCodes.Validation);
                }

                return Ok(99);
            },
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(99, result.Value);
        Assert.Equal(new[] { 1, 2, 3 }, attempts);
    }

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ValueTaskDelegate_ShouldRetryUntilSuccess()
    {
        int attempt = 0;

        var result = await RetryAsync(
            async (currentAttempt, ct) =>
            {
                await Task.Delay(5, ct);
                attempt = currentAttempt;
                if (currentAttempt < 2)
                {
                    return Err<int>("retry", ErrorCodes.Validation);
                }

                return Ok(123);
            },
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(123, result.Value);
        Assert.Equal(2, attempt);
    }

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldSurfaceFinalFailure()
    {
        var attempts = 0;

        var result = await RetryAsync(
            (attempt, ct) =>
            {
                attempts = attempt;
                return Task.FromResult(Err<int>("still failing", ErrorCodes.Validation));
            },
            maxAttempts: 2,
            initialDelay: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryAsync_ShouldPropagateCancellationWithoutRetrying(bool useLinkedToken)
    {
        var attempts = new List<int>();

        var result = await RetryAsync(
            async (attempt, ct) =>
            {
                attempts.Add(attempt);

                if (attempt == 1)
                {
                    if (useLinkedToken)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        await linked.CancelAsync();
                        throw new OperationCanceledException(linked.Token);
                    }

                    throw new OperationCanceledException();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                return Ok(1);
            },
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.Equal(new[] { 1 }, attempts);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldThrowWhenOperationsNull() => await Assert.ThrowsAsync<ArgumentNullException>(static async () =>
                                                                              await FanOutAsync<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldThrowWhenOperationEntryNull()
    {
        var operations = new Func<CancellationToken, Task<Result<int>>>?[]
        {
            ct => Task.FromResult(Ok(1)),
            null
        };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await FanOutAsync(operations!, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("operations", exception.ParamName);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldReturnEmptyWhenNoOperations()
    {
        var result = await FanOutAsync(Array.Empty<Func<CancellationToken, Task<Result<int>>>>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldPropagateFailure()
    {
        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            static _ => Task.FromResult(Ok(1)),
            static _ => Task.FromResult(Err<int>("failed", ErrorCodes.Validation))
        };

        var result = await FanOutAsync(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithExecutionPolicy_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        TaskCompletionSource[] started =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];

        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            async ct =>
            {
                started[0].TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(1);
            },
            async ct =>
            {
                started[1].TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(2);
            }
        };

        ValueTask<Result<IReadOnlyList<int>>> task = FanOutAsync(operations, cancellationToken: cts.Token);
        await Task.WhenAll(started.Select(tcs => tcs.Task));

        await cts.CancelAsync();
        Result<IReadOnlyList<int>> result = await task;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldThrowWhenOperationsNull() => await Assert.ThrowsAsync<ArgumentNullException>(static async () =>
                                                                            await RaceAsync<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldThrowWhenOperationEntryNull()
    {
        var operations = new Func<CancellationToken, Task<Result<int>>>?[]
        {
            _ => Task.FromResult(Ok(1)),
            null
        };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await RaceAsync(operations!, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("operations", exception.ParamName);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldReturnFailureWhenAllFail()
    {
        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            static _ => Task.FromResult(Err<int>("first failure", ErrorCodes.Validation)),
            static _ => Task.FromResult(Err<int>("second failure", ErrorCodes.Validation))
        };

        var result = await RaceAsync(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Aggregate, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldReturnCanceledWhenOperationsCanceled()
    {
        using var cts = new CancellationTokenSource();
        TaskCompletionSource[] started =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];

        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            async ct =>
            {
                started[0].TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(1);
            },
            async ct =>
            {
                started[1].TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(2);
            }
        };

        ValueTask<Result<int>> task = RaceAsync(operations, cancellationToken: cts.Token);
        await Task.WhenAll(started.Select(tcs => tcs.Task));

        await cts.CancelAsync();
        Result<int> result = await task;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnSuccessWhenOperationCompletes()
    {
        var result = await WithTimeoutAsync(
            static _ => Task.FromResult(Ok(5)),
            TimeSpan.FromSeconds(5),
            timeProvider: new FakeTimeProvider(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnSuccessWithInfiniteTimeout()
    {
        var result = await WithTimeoutAsync(
            static _ => Task.FromResult(Ok(7)),
            Timeout.InfiniteTimeSpan,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldThrowWhenTimeoutNegative() => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(static async () =>
                                                                                    await WithTimeoutAsync(static _ => Task.FromResult(Ok(1)), TimeSpan.FromMilliseconds(-2), cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldPropagateOperationFailure()
    {
        var result = await WithTimeoutAsync(
            static _ => Task.FromResult(Err<int>("failure", ErrorCodes.Validation)),
            TimeSpan.FromSeconds(5),
            timeProvider: new FakeTimeProvider(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnCanceledWhenOperationThrowsWithoutToken()
    {
        var result = await WithTimeoutAsync<int>(
            static _ => throw new OperationCanceledException(),
            TimeSpan.FromSeconds(5),
            timeProvider: new FakeTimeProvider(),
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.False(result.Error!.TryGetMetadata("cancellationToken", out CancellationToken _));
    }

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldThrowWhenOperationNull() => await Assert.ThrowsAsync<ArgumentNullException>(static async () =>
                                                                            await RetryAsync<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RetryAsync_ShouldThrowWhenMaxAttemptsInvalid(int maxAttempts) => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                                                                                                await RetryAsync((_, _) => Task.FromResult(Ok(1)), maxAttempts, initialDelay: TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldReturnFailureWhenOperationThrows()
    {
        var result = await RetryAsync<int>(
            static (_, _) => throw new InvalidOperationException("boom"),
            maxAttempts: 2,
            initialDelay: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains("boom", result.Error?.Message);
    }
}
