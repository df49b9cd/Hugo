using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public partial class GoTests
{
    [Fact]
    public async Task FanOutAsync_Delegates_ShouldReturnAllResults()
    {
        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                return Ok(1);
            },
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
                return Ok(2);
            }
        };

        var result = await FanOutAsync(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 1, 2 }, result.Value.ToArray());
    }

    [Fact]
    public async Task RaceAsync_ShouldReturnFirstSuccessfulResult()
    {
        var operations = new List<Func<CancellationToken, Task<Result<int>>>>
        {
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
                return Err<int>("slow failure", ErrorCodes.Validation);
            },
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                return Ok(2);
            },
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(30), ct);
                return Err<int>("failure", ErrorCodes.Validation);
            }
        };

        var result = await RaceAsync(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
    }

    [Fact]
    public async Task WithTimeoutAsync_ShouldReturnTimeoutError()
    {
        var provider = new FakeTimeProvider();

        var timeoutTask = WithTimeoutAsync(
            async ct =>
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

    [Fact]
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

    [Fact]
    public async Task WithTimeoutAsync_ShouldReturnFailure_WhenOperationThrows()
    {
        var provider = new FakeTimeProvider();

        var result = await WithTimeoutAsync<int>(
            _ => throw new InvalidOperationException("boom"),
            TimeSpan.FromSeconds(5),
            timeProvider: provider,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains("boom", result.Error?.Message);
    }

    [Fact]
    public async Task WithTimeoutAsync_ShouldReturnFailure_WhenOperationThrows_WithInfiniteTimeout()
    {
        var result = await WithTimeoutAsync<int>(
            _ => throw new InvalidOperationException("boom infinite"),
            Timeout.InfiniteTimeSpan,
            timeProvider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains("boom infinite", result.Error?.Message);
    }

    [Fact]
    public async Task RetryAsync_ShouldRetryUntilSuccess()
    {
        var attempts = new List<int>();

        var result = await RetryAsync(
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
        Assert.Equal(new[] { 1, 2, 3 }, attempts.ToArray());
    }

    [Fact]
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
}
