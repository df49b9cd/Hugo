using Hugo.Policies;

using Shouldly;

namespace Hugo.Tests;

public class GoRaceValueTaskAsyncTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask RaceValueTaskAsync_ShouldReturnFastestSuccessfulResult()
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

        var result = await Go.RaceAsync(
            operations,
            compensationPolicy,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RaceValueTaskAsync_ShouldAggregateFailures_WhenAllOperationsFail()
    {
        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("first failure", ErrorCodes.Validation))),
            static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("second failure", ErrorCodes.Validation)))
        };

        var result = await Go.RaceAsync(
            operations,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error!.Code.ShouldBe(ErrorCodes.Aggregate);
        result.Error.Metadata.TryGetValue("errors", out var nested).ShouldBeTrue();
        var innerErrors = nested.ShouldBeOfType<Error[]>();
        innerErrors.Length.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RaceValueTaskAsync_ShouldCancelLosingOperations()
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

        var result = await Go.RaceAsync(
            operations,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var completion = await Task.WhenAny(
            loserCanceled.Task,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        completion.ShouldBe(loserCanceled.Task);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RaceValueTaskAsync_ShouldReturnCanceled_WhenCallerCancels()
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

        var raceTask = Go.RaceAsync(
            operations,
            cancellationToken: linkedCts.Token);

        await cts.CancelAsync();

        try
        {
            var result = await raceTask;
            result.IsFailure.ShouldBeTrue();
            result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
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
