using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Hugo.Policies;
using Hugo.Sagas;

namespace Hugo.Tests;

internal class ResultPipelineEnhancementsTests
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
    public async Task WhenAll_ShouldReturnEmptyResult_WhenNoOperations()
    {
        var result = await Result.WhenAll(Array.Empty<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task WhenAll_ShouldThrow_WhenOperationNull()
    {
        var operations = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>?[] { null };

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Result.WhenAll(operations!, cancellationToken: TestContext.Current.CancellationToken));
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
            await Task.Delay(1, token);
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
    public async Task WhenAny_ShouldReturnValidationError_WhenNoOperations()
    {
        var result = await Result.WhenAny(Array.Empty<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Equal("No operations provided for WhenAny.", result.Error?.Message);
    }

    [Fact]
    public async Task WhenAny_ShouldAggregateFailures_WhenNoSuccess()
    {
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>(static (_, _) =>
                ValueTask.FromResult(Result.Fail<int>(Error.From("first failure", ErrorCodes.Validation)))),
            static (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("second failure", ErrorCodes.Exception)))
        };

        var result = await Result.WhenAny(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("All operations failed.", result.Error?.Message);
        Assert.True(result.Error!.Metadata.TryGetValue("errors", out var nested));
        var errors = Assert.IsType<Error[]>(nested);
        Assert.Equal(2, errors.Length);
    }

    [Fact]
    public async Task WhenAny_ShouldReturnCanceledError_WhenAllOperationsCanceled()
    {
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>(static (_, _) =>
                ValueTask.FromResult(Result.Fail<int>(Error.Canceled()))),
            static (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.Canceled()))
        };

        var result = await Result.WhenAny(operations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.Equal("All operations were canceled.", result.Error?.Message);
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
            .AddStep("reserve", static (_, _) => ValueTask.FromResult(Result.Ok("reserveId")))
            .AddStep("charge", static (context, _) =>
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
        var data = GetSequence(TestContext.Current.CancellationToken);

        await data.ToChannelAsync(channel.Writer, TestContext.Current.CancellationToken);

        var collected = new List<int>();
        await foreach (var item in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(item.IsSuccess);
            collected.Add(item.Value);
        }

        Assert.Equal(new[] { 1, 2, 3 }, collected);

        static async IAsyncEnumerable<Result<int>> GetSequence([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Delay(10, ct);
            yield return Result.Ok(2);
            yield return Result.Ok(3);
        }
    }

    [Fact]
    public async Task FanInAsync_ShouldMergeSourcesAndCompleteWriter()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();

        static async IAsyncEnumerable<Result<int>> Source(int start, [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = start; i < start + 2; i++)
            {
                await Task.Delay(5, ct);
                yield return Result.Ok(i);
            }
        }

        await Result.FanInAsync(new[] { Source(0, TestContext.Current.CancellationToken), Source(2, TestContext.Current.CancellationToken) }, channel.Writer, TestContext.Current.CancellationToken);

        var collected = new List<int>();
        await foreach (var result in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(result.IsSuccess);
            collected.Add(result.Value);
        }

        collected.Sort();
        Assert.Equal([0, 1, 2, 3], collected);
    }

    [Fact]
    public async Task FanInAsync_ShouldPropagateSourceFailure()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();

        static async IAsyncEnumerable<Result<int>> Faulty([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Delay(5, ct);
            throw new InvalidOperationException("fan-in failure");
        }

        static async IAsyncEnumerable<Result<int>> Healthy([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(10);
            await Task.Delay(5, ct);
            yield return Result.Ok(11);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Result.FanInAsync(new[] { Faulty(TestContext.Current.CancellationToken), Healthy(TestContext.Current.CancellationToken) }, channel.Writer, TestContext.Current.CancellationToken));

        var buffered = new List<Result<int>>();
        while (channel.Reader.TryRead(out var item))
        {
            buffered.Add(item);
        }

        Assert.Contains(buffered, result => result.IsSuccess && result.Value == 1);
        Assert.True(channel.Reader.Completion.IsFaulted);
        var completionException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.Reader.Completion);
        Assert.Same(exception, completionException);
    }

    [Fact]
    public async Task FanOutAsync_ShouldBroadcastResultsToAllWriters()
    {
        var first = Channel.CreateUnbounded<Result<int>>();
        var second = Channel.CreateUnbounded<Result<int>>();

        static async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(3);
            await Task.Delay(5, ct);
            yield return Result.Ok(4);
        }

        await Result.FanOutAsync(Source(TestContext.Current.CancellationToken), [first.Writer, second.Writer], TestContext.Current.CancellationToken);

        var firstValues = await ReadAllValuesAsync(first.Reader);
        var secondValues = await ReadAllValuesAsync(second.Reader);

        Assert.Equal([3, 4], firstValues);
        Assert.Equal([3, 4], secondValues);
    }

    [Fact]
    public async Task PartitionAsync_ShouldSplitResultsAndCompleteWriters()
    {
        var source = GetSequence();
        var evenWriter = Channel.CreateUnbounded<Result<int>>();
        var oddWriter = Channel.CreateUnbounded<Result<int>>();

        await source.PartitionAsync(static value => value % 2 == 0, evenWriter.Writer, oddWriter.Writer, TestContext.Current.CancellationToken);

        var evenResults = await ReadAllResultsAsync(evenWriter.Reader);
        var oddResults = await ReadAllResultsAsync(oddWriter.Reader);

        Assert.All(evenResults, static result => Assert.True(result.IsSuccess));
        Assert.Equal([2], evenResults.Select(static result => result.Value).ToArray());

        Assert.Equal(2, oddResults.Count);
        Assert.True(oddResults[0].IsSuccess);
        Assert.Equal(1, oddResults[0].Value);
        Assert.True(oddResults[1].IsFailure);

        static async IAsyncEnumerable<Result<int>> GetSequence()
        {
            yield return Result.Ok(1);
            yield return Result.Ok(2);
            yield return Result.Fail<int>(Error.From("failure"));
        }
    }

    [Fact]
    public async Task ToChannelAsync_ShouldEmitCanceledSentinelWhenEnumerationCanceled()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();
        using var cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);

        var toChannelTask = Sequence(linkedCts.Token).ToChannelAsync(channel.Writer, linkedCts.Token).AsTask();

        var first = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);

        cts.Cancel();

        await toChannelTask;

        var sentinel = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(sentinel.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, sentinel.Error?.Code);
        await channel.Reader.Completion;

        static async IAsyncEnumerable<Result<int>> Sequence([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            yield return Result.Ok(2);
        }
    }

    [Fact]
    public async Task WindowAsync_ShouldBatchValues()
    {
        var source = GetSequence(TestContext.Current.CancellationToken);
        var batches = new List<IReadOnlyList<int>>();
        await foreach (var batch in source.WindowAsync(2, TestContext.Current.CancellationToken))
        {
            Assert.True(batch.IsSuccess);
            batches.Add(batch.Value);
        }

        Assert.Equal(2, batches.Count);
        Assert.Equal([1, 2], batches[0]);
        Assert.Equal([3], batches[1]);

        static async IAsyncEnumerable<Result<int>> GetSequence([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Delay(5, ct);
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

        var grouped = Result.Group(data, static value => value % 2);
        Assert.True(grouped.IsSuccess);
        Assert.Equal(2, grouped.Value.Count);

        var partitioned = Result.Partition(data, static value => value % 2 == 0);
        Assert.True(partitioned.IsSuccess);
        Assert.Equal([2, 4], partitioned.Value.True);
        Assert.Equal([1, 3], partitioned.Value.False);

        var windowed = Result.Window(data, 3);
        Assert.True(windowed.IsSuccess);
        Assert.Equal(2, windowed.Value.Count);
    }

    [Fact]
    public void HigherOrderOperators_ShouldSurfaceFailure()
    {
        var error = Error.From("fail");
        var data = new[]
        {
            Result.Ok(1),
            Result.Fail<int>(error)
        };

        var grouped = Result.Group(data, value => value);
        Assert.True(grouped.IsFailure);
        Assert.Same(error, grouped.Error);

        var partitioned = Result.Partition(data, value => value > 0);
        Assert.True(partitioned.IsFailure);
        Assert.Same(error, partitioned.Error);

        var windowed = Result.Window(data, 2);
        Assert.True(windowed.IsFailure);
        Assert.Same(error, windowed.Error);

        Assert.Throws<ArgumentOutOfRangeException>(() => Result.Window(data, 0));
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

    [Fact]
    public async Task RetryWithPolicyAsync_ShouldReturnFailureAfterExhaustingRetries()
    {
        var attempts = 0;
        var policy = new ResultExecutionPolicy(ResultRetryPolicy.FixedDelay(3, TimeSpan.Zero), Compensation: ResultCompensationPolicy.SequentialReverse);

        var result = await Result.RetryWithPolicyAsync((_, _) =>
        {
            attempts++;
            return ValueTask.FromResult(Result.Fail<int>(Error.From("still failing", ErrorCodes.Exception)));
        }, policy, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(3, attempts);
        Assert.Equal("still failing", result.Error?.Message);
    }

    private static async Task<int[]> ReadAllValuesAsync(ChannelReader<Result<int>> reader)
    {
        var values = new List<int>();
        await foreach (var result in reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(result.IsSuccess);
            values.Add(result.Value);
        }

        return values.ToArray();
    }

    private static async Task<List<Result<int>>> ReadAllResultsAsync(ChannelReader<Result<int>> reader)
    {
        var results = new List<Result<int>>();
        await foreach (var result in reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            results.Add(result);
        }

        return results;
    }
}
