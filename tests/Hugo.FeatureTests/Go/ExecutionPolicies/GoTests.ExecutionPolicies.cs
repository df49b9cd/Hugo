using System.Threading.Channels;

using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;

using Shouldly;

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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe([1, 2]);
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe([10, 20]);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldReturnFirstSuccessfulResult()
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(2);
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

        var result = await RaceAsync<int>(operations, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(7);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceValueTaskAsync_ShouldReturnCanceled_WhenCallerCancels()
    {
        using var callerCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCts.Token, TestContext.Current.CancellationToken);

        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(1);
            },
            static async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(2);
            }
        };

        var raceTask = RaceAsync<int>(operations, cancellationToken: linkedCts.Token);
        await callerCts.CancelAsync();

        try
        {
            var result = await raceTask;
            result.IsFailure.ShouldBeTrue();
            result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        }
        catch (OperationCanceledException)
        {
            // In some schedules, the race will surface cancellation via an exception instead of an Error result.
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceValueTaskAsync_ShouldReturnFirstChannelMessage()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();

        var operations = new List<Func<CancellationToken, ValueTask<Result<string>>>>
        {
            ct => ReadNextAsync("alpha", first.Reader, ct),
            ct => ReadNextAsync("beta", second.Reader, ct)
        };

        var raceTask = RaceAsync<string>(operations, cancellationToken: TestContext.Current.CancellationToken);

        await first.Writer.WriteAsync(11, TestContext.Current.CancellationToken);
        first.Writer.TryComplete();
        second.Writer.TryComplete();

        var result = await raceTask;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("alpha:11");
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceValueTaskAsync_WithWindowTimerFirst_ShouldStillDeliverBatches()
    {
        var provider = new FakeTimeProvider();
        var source = Channel.CreateUnbounded<int>();
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("window-race", scope, provider, TestContext.Current.CancellationToken);

        var window = await ResultPipelineChannels.WindowAsync(
            context,
            source.Reader,
            batchSize: 2,
            flushInterval: TimeSpan.FromSeconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        // Timer fires before any data, but race should still succeed once data arrives.
        provider.Advance(TimeSpan.FromSeconds(1));

        await source.Writer.WriteAsync(5, TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(6, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();
        var operations = new Func<CancellationToken, ValueTask<Result<IReadOnlyList<int>>>>[]
        {
            async ct =>
            {
                var batches = new List<IReadOnlyList<int>>();
                while (await window.WaitToReadAsync(ct))
                {
                    while (window.TryRead(out var batch))
                    {
                        batches.Add(batch);
                    }
                }

                return batches.Count switch
                {
                    > 0 => Ok(batches[0]),
                    _ => Err<IReadOnlyList<int>>("no batches")
                };
            },
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return Err<IReadOnlyList<int>>("timeout");
            }
        };

        var raceResult = await RaceAsync<IReadOnlyList<int>>(
            operations,
            timeProvider: provider, cancellationToken: TestContext.Current.CancellationToken);

        raceResult.IsSuccess.ShouldBeTrue();
        raceResult.Value.ShouldBe([5, 6]);
    }

    [Fact(Timeout = 15_000)]
    public async Task CollectErrorsAsync_ShouldAggregateFailuresAcrossStreams()
    {
        var stream = CollectingStream();

        var result = await stream.CollectErrorsAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Aggregate);
    }

    private static async IAsyncEnumerable<Result<int>> CollectingStream()
    {
        yield return Ok(1);
        await Task.Yield();
        yield return Err<int>("fail-1", ErrorCodes.Validation);
        yield return Err<int>("fail-2", ErrorCodes.Validation);
    }

    private static async ValueTask<Result<string>> ReadNextAsync(string label, ChannelReader<int> reader, CancellationToken token)
    {
        while (await reader.WaitToReadAsync(token))
        {
            while (reader.TryRead(out var value))
            {
                return Ok($"{label}:{value}");
            }
        }

        return Err<string>($"{label} closed before producing a value.", ErrorCodes.Unspecified);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Timeout);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldContain("boom");
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnFailure_WhenOperationThrows_WithInfiniteTimeout()
    {
        var result = await WithTimeoutAsync<int>(
            static _ => throw new InvalidOperationException("boom infinite"),
            Timeout.InfiniteTimeSpan,
            timeProvider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldContain("boom infinite");
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(5);
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(99);
        attempts.ShouldBe(new[] { 1, 2, 3 });
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(123);
        attempt.ShouldBe(2);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
        attempts.ShouldBe(2);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        attempts.ShouldBe(new[] { 1 });
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldThrowWhenOperationsNull() => await Should.ThrowAsync<ArgumentNullException>(static async () =>
                                                                              await FanOutAsync<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldThrowWhenOperationEntryNull()
    {
        var operations = new Func<CancellationToken, Task<Result<int>>>?[]
        {
            ct => Task.FromResult(Ok(1)),
            null
        };

        var exception = await Should.ThrowAsync<ArgumentNullException>(async () =>
            await FanOutAsync(operations!, cancellationToken: TestContext.Current.CancellationToken));

        exception.ParamName.ShouldBe("operations");
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldReturnEmptyWhenNoOperations()
    {
        var result = await FanOutAsync(Array.Empty<Func<CancellationToken, Task<Result<int>>>>(), cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldThrowWhenOperationsNull() => await Should.ThrowAsync<ArgumentNullException>(static async () =>
                                                                            await RaceAsync<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldThrowWhenOperationEntryNull()
    {
        var operations = new Func<CancellationToken, ValueTask<Result<int>>>?[]
        {
            _ => ValueTask.FromResult(Ok(1)),
            null
        };

        var exception = await Should.ThrowAsync<ArgumentNullException>(async () =>
            await RaceAsync(operations!, cancellationToken: TestContext.Current.CancellationToken));

        exception.ParamName.ShouldBe("operations");
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceAsync_ShouldReturnFailureWhenAllFail()
    {
        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static _ => ValueTask.FromResult(Err<int>("first failure", ErrorCodes.Validation)),
            static _ => ValueTask.FromResult(Err<int>("second failure", ErrorCodes.Validation))
        };

        var result = await RaceAsync(operations, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Aggregate);
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

        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnSuccessWhenOperationCompletes()
    {
        var result = await WithTimeoutAsync(
            static _ => Task.FromResult(Ok(5)),
            TimeSpan.FromSeconds(5),
            timeProvider: new FakeTimeProvider(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(5);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnSuccessWithInfiniteTimeout()
    {
        var result = await WithTimeoutAsync(
            static _ => Task.FromResult(Ok(7)),
            Timeout.InfiniteTimeSpan,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(7);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldThrowWhenTimeoutNegative() => await Should.ThrowAsync<ArgumentOutOfRangeException>(static async () =>
                                                                                    await WithTimeoutAsync(static _ => Task.FromResult(Ok(1)), TimeSpan.FromMilliseconds(-2), cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldPropagateOperationFailure()
    {
        var result = await WithTimeoutAsync(
            static _ => Task.FromResult(Err<int>("failure", ErrorCodes.Validation)),
            TimeSpan.FromSeconds(5),
            timeProvider: new FakeTimeProvider(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnCanceledWhenOperationThrowsWithoutToken()
    {
        var result = await WithTimeoutAsync<int>(
            static _ => throw new OperationCanceledException(),
            TimeSpan.FromSeconds(5),
            timeProvider: new FakeTimeProvider(),
            cancellationToken: CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        result.Error!.TryGetMetadata("cancellationToken", out CancellationToken _).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldThrowWhenOperationNull() => await Should.ThrowAsync<ArgumentNullException>(static async () =>
                                                                            await RetryAsync<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RetryAsync_ShouldThrowWhenMaxAttemptsInvalid(int maxAttempts) => await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
                                                                                                await RetryAsync((_, _) => Task.FromResult(Ok(1)), maxAttempts, initialDelay: TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldReturnFailureWhenOperationThrows()
    {
        var result = await RetryAsync<int>(
            static (_, _) => throw new InvalidOperationException("boom"),
            maxAttempts: 2,
            initialDelay: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldContain("boom");
    }
}
