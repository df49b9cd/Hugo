using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Hugo.Policies;

namespace Hugo.Tests;

public class GoRaceValueTaskAsyncTests
{
    [Fact(Timeout = 15_000)]
    public async Task RaceValueTaskAsync_ShouldReturnFastestSuccessfulResult()
    {
        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                return Result.Ok(1);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
                return Result.Ok(2);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                return Result.Ok(3);
            }
        };

        var compensationPolicy = ResultExecutionPolicy
            .None
            .WithCompensation(ResultCompensationPolicy.SequentialReverse);

        var result = await Go.RaceValueTaskAsync(
            operations,
            compensationPolicy,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceValueTaskAsync_ShouldAggregateFailures_WhenAllOperationsFail()
    {
        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("first failure", ErrorCodes.Validation))),
            static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("second failure", ErrorCodes.Validation)))
        };

        var result = await Go.RaceValueTaskAsync(
            operations,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.Aggregate, result.Error!.Code);
        Assert.True(result.Error.Metadata.TryGetValue("errors", out var nested));
        var innerErrors = Assert.IsType<Error[]>(nested);
        Assert.Equal(2, innerErrors.Length);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceValueTaskAsync_ShouldCancelLosingOperations()
    {
        var loserCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                return Result.Ok(42);
            },
            ct => AwaitCancellationAsync(ct, loserCanceled)
        };

        var result = await Go.RaceValueTaskAsync(
            operations,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        await loserCanceled.Task.WaitAsync(timeoutCts.Token);
    }

    [Fact(Timeout = 15_000)]
    public async Task RaceValueTaskAsync_ShouldReturnCanceled_WhenCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);

        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Result.Ok(10);
            },
            static async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Result.Ok(20);
            }
        };

        var raceTask = Go.RaceValueTaskAsync(
            operations,
            cancellationToken: linkedCts.Token);

        cts.Cancel();

        try
        {
            var result = await raceTask;
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        }
        catch (OperationCanceledException)
        {
            // Expected when the race observes cancellation before producing results.
        }
    }

    private static async ValueTask<Result<int>> AwaitCancellationAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> observer)
    {
        using var registration = cancellationToken.Register(() => observer.TrySetResult(true));

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Fail<int>(Error.Canceled(token: cancellationToken));
        }

        return Result.Ok(0);
    }
}
