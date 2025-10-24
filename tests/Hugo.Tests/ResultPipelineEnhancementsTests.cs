using System.Threading.Channels;

using Hugo.Policies;
using Hugo.Sagas;

namespace Hugo.Tests;

public class ResultPipelineEnhancementsTests
{
    [Fact]
    public async Task WhenAll_ShouldReturnValues_WhenAllStepsSucceed()
    {
        var compensations = 0;
        var operations = new List<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>
        {
            async (context, token) =>
            {
                await Task.Delay(10, token);
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensations);
                    return ValueTask.CompletedTask;
                });
                return Result.Ok(1);
            },
            (context, _) =>
            {
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensations);
                    return ValueTask.CompletedTask;
                });
                return ValueTask.FromResult(Result.Ok(2));
            }
        };

        var result = await Result.WhenAll(operations, policy: new ResultExecutionPolicy(Compensation: ResultCompensationPolicy.SequentialReverse), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2], result.Value);
        Assert.Equal(0, compensations);
    }

    [Fact]
    public async Task WhenAll_ShouldRunCompensation_OnFailure()
    {
        var compensationInvoked = 0;
        var operations = new List<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>
        {
            (context, _) =>
            {
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensationInvoked);
                    return ValueTask.CompletedTask;
                });
                return ValueTask.FromResult(Result.Ok(10));
            },
            (context, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Exception)))
        };

        var policy = new ResultExecutionPolicy(Compensation: ResultCompensationPolicy.SequentialReverse);
        var result = await Result.WhenAll(operations, policy, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(1, compensationInvoked);
    }

    [Fact]
    public async Task WhenAny_ShouldSelectFirstSuccess_AndCompensateOthers()
    {
        var compensation = 0;

        var fast = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<string>>>(async (context, token) =>
        {
            context.RegisterCompensation(ct =>
            {
                Interlocked.Increment(ref compensation);
                return ValueTask.CompletedTask;
            });
            await Task.Delay(1);
            return Result.Ok("fast");
        });

        var slow = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<string>>>(async (context, token) =>
        {
            context.RegisterCompensation(ct =>
            {
                Interlocked.Increment(ref compensation);
                return ValueTask.CompletedTask;
            });
            await Task.Delay(200);
            return Result.Ok("slow");
        });

        var policy = new ResultExecutionPolicy(Compensation: ResultCompensationPolicy.SequentialReverse);
        var result = await Result.WhenAny([fast, slow], policy, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("fast", result.Value);
        Assert.Equal(1, compensation); // slow compensation should fire.
    }

    [Fact]
    public async Task ResultSaga_ShouldRollback_WhenStepFails()
    {
        var compensation = 0;

        var saga = new ResultSagaBuilder()
            .AddStep("reserve", async (context, token) =>
            {
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensation);
                    return ValueTask.CompletedTask;
                });
                return Result.Ok("reservation");
            })
            .AddStep("charge", (_, _) => ValueTask.FromResult(Result.Fail<string>(Error.From("payment failed", ErrorCodes.Exception))));

        var policy = new ResultExecutionPolicy(Compensation: ResultCompensationPolicy.SequentialReverse);
        var result = await saga.ExecuteAsync(policy, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(1, compensation);
    }

    [Fact]
    public async Task ResultSaga_ShouldReturnState_OnSuccess()
    {
        var saga = new ResultSagaBuilder()
            .AddStep("reserve", (_, _) => ValueTask.FromResult(Result.Ok("reserveId")))
            .AddStep("charge", (context, _) =>
            {
                var reserveId = context.State.TryGet("reserve", out string? id) ? id : string.Empty;
                return ValueTask.FromResult(Result.Ok(reserveId + "-charged"));
            }, resultKey: "chargeResult");

        var result = await saga.ExecuteAsync(ResultExecutionPolicy.None, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.TryGet("chargeResult", out string? charge));
        Assert.Equal("reserveId-charged", charge);
    }

    [Fact]
    public async Task StreamingExtensions_ShouldBridgeChannelsAndEnumerables()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();
        var data = GetSequence();

        await data.ToChannelAsync(channel.Writer, TestContext.Current.CancellationToken);

        var collected = new List<int>();
        await foreach (var item in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(item.IsSuccess);
            collected.Add(item.Value);
        }

        Assert.Equal(new[] { 1, 2, 3 }, collected);

        static async IAsyncEnumerable<Result<int>> GetSequence()
        {
            yield return Result.Ok(1);
            await Task.Delay(10);
            yield return Result.Ok(2);
            yield return Result.Ok(3);
        }
    }

    [Fact]
    public async Task WindowAsync_ShouldBatchValues()
    {
        var source = GetSequence();
        var batches = new List<IReadOnlyList<int>>();
        await foreach (var batch in source.WindowAsync(2, TestContext.Current.CancellationToken))
        {
            Assert.True(batch.IsSuccess);
            batches.Add(batch.Value);
        }

        Assert.Equal(2, batches.Count);
        Assert.Equal([1, 2], batches[0]);
        Assert.Equal([3], batches[1]);

        static async IAsyncEnumerable<Result<int>> GetSequence()
        {
            yield return Result.Ok(1);
            await Task.Delay(5);
            yield return Result.Ok(2);
            yield return Result.Ok(3);
        }
    }

    [Fact]
    public void HigherOrderOperators_ShouldGroupPartitionAndWindow()
    {
        var data = new[]
        {
            Result.Ok(1),
            Result.Ok(2),
            Result.Ok(3),
            Result.Ok(4)
        };

        var grouped = Result.Group(data, value => value % 2);
        Assert.True(grouped.IsSuccess);
        Assert.Equal(2, grouped.Value.Count);

        var partitioned = Result.Partition(data, value => value % 2 == 0);
        Assert.True(partitioned.IsSuccess);
        Assert.Equal([2, 4], partitioned.Value.True);
        Assert.Equal([1, 3], partitioned.Value.False);

        var windowed = Result.Window(data, 3);
        Assert.True(windowed.IsSuccess);
        Assert.Equal(2, windowed.Value.Count);
    }

    [Fact]
    public async Task RetryWithPolicyAsync_ShouldRetryUntilSuccess()
    {
        var attempts = 0;
        var policy = new ResultExecutionPolicy(ResultRetryPolicy.FixedDelay(3, TimeSpan.FromMilliseconds(5)), Compensation: ResultCompensationPolicy.SequentialReverse);

        var result = await Result.RetryWithPolicyAsync(async (context, token) =>
        {
            attempts++;
            if (attempts < 3)
            {
                return Result.Fail<int>(Error.From("retry", ErrorCodes.Timeout));
            }

            context.RegisterCompensation(static ct => ValueTask.CompletedTask);
            return Result.Ok(42);
        }, policy, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(3, attempts);
    }
}
