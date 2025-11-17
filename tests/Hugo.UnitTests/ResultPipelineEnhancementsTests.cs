using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Shouldly;

using Hugo.Policies;
using Hugo.Sagas;

namespace Hugo.Tests;

public class ResultPipelineEnhancementsTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldReturnValues_WhenAllStepsSucceed()
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe([1, 2]);
        compensations.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldReturnEmptyResult_WhenNoOperations()
    {
        var result = await Result.WhenAll(Array.Empty<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>(), cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldThrow_WhenOperationNull()
    {
        var operations = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>?[] { null };

        await Should.ThrowAsync<ArgumentNullException>(async () => await Result.WhenAll(operations!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldRunCompensation_OnFailure()
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
        var result = await Result.WhenAll(operations, policy, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        compensationInvoked.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAny_ShouldSelectFirstSuccess_AndCompensateOthers()
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
        var result = await Result.WhenAny([fast, slow], policy, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("fast");
        compensation.ShouldBe(1); // slow compensation should fire.
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAny_ShouldReturnSuccess_WhenPeerFailsAfterWinner()
    {
        var winnerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var winner = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<string>>>((_, _) =>
        {
            winnerReleased.TrySetResult();
            return ValueTask.FromResult(Result.Ok("winner"));
        });

        var failingPeer = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<string>>>(async (_, _) =>
        {
            await winnerReleased.Task;
            await Task.Delay(10);
            return Result.Fail<string>(Error.From("peer failed", ErrorCodes.Exception));
        });

        var result = await Result.WhenAny([winner, failingPeer], cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("winner");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAny_ShouldIgnorePostWinnerCancellations()
    {
        var winner = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((_, _) =>
            ValueTask.FromResult(Result.Ok(1)));

        var canceledPeer = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result.Ok(2);
        });

        var result = await Result.WhenAny([winner, canceledPeer], cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAny_ShouldNotHang_WhenCallerTokenAlreadyCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ops = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((_, _) => ValueTask.FromResult(Result.Ok(1))),
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((_, _) => ValueTask.FromResult(Result.Ok(2)))
        };

        var result = await Result.WhenAny(ops, cancellationToken: cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAny_ShouldReturnValidationError_WhenNoOperations()
    {
        var result = await Result.WhenAny(Array.Empty<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>(), cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
        result.Error?.Message.ShouldBe("No operations provided for WhenAny.");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAny_ShouldAggregateFailures_WhenNoWinner()
    {
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>(static (_, _) =>
                ValueTask.FromResult(Result.Fail<int>(Error.From("first failure", ErrorCodes.Validation)))),
            static (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("second failure", ErrorCodes.Exception)))
        };

        var result = await Result.WhenAny(operations, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldBe("All operations failed.");
        result.Error!.Metadata.TryGetValue("errors", out var nested).ShouldBeTrue();
        var errors = nested.ShouldBeOfType<Error[]>();
        errors.Length.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAny_ShouldReturnCanceledError_WhenAllOperationsCanceled()
    {
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>(static (_, _) =>
                ValueTask.FromResult(Result.Fail<int>(Error.Canceled()))),
            static (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.Canceled()))
        };

        var result = await Result.WhenAny(operations, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        result.Error?.Message.ShouldBe("All operations were canceled.");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ResultSaga_ShouldRollback_WhenStepFails()
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

        result.IsFailure.ShouldBeTrue();
        compensation.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldRunCompensation_OnExternalCancellation()
    {
        using var cts = new CancellationTokenSource();
        var compensationInvocations = 0;
        var compensationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var policy = ResultExecutionPolicy.None
            .WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 2, delay: TimeSpan.FromSeconds(5)))
            .WithCompensation(ResultCompensationPolicy.SequentialReverse);
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((context, token) =>
            {
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensationInvocations);
                    compensationFinished.TrySetResult();
                    return ValueTask.CompletedTask;
                });

                return ValueTask.FromResult(Result.Ok(1));
            }),
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((_, token) =>
            {
                retryDelayStarted.TrySetResult();
                return ValueTask.FromResult(Result.Fail<int>(Error.From("peer failed", ErrorCodes.Exception)));
            })
        };

        var aggregateTask = Result.WhenAll(operations, policy: policy, cancellationToken: cts.Token);

        await retryDelayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();

        var result = await aggregateTask;

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        await compensationFinished.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        compensationInvocations.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldRunCompensation_OnOperationCanceledException()
    {
        var compensationInvocations = 0;
        var firstCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compensationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((context, token) =>
            {
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensationInvocations);
                    compensationFinished.TrySetResult();
                    return ValueTask.CompletedTask;
                });

                firstCompleted.TrySetResult();
                return ValueTask.FromResult(Result.Ok(1));
            }),
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>(async (_, _) =>
            {
                await firstCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
                using var localCts = new CancellationTokenSource();
                localCts.Cancel();
                throw new OperationCanceledException(localCts.Token);
            })
        };

        var result = await Result.WhenAll(operations, policy: policy, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        await compensationFinished.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        compensationInvocations.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldSkipCompensation_WhenNoTasksCompleted()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var compensationInvocations = 0;

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((context, token) =>
            {
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensationInvocations);
                    return ValueTask.CompletedTask;
                });
                return ValueTask.FromResult(Result.Ok(1));
            }),
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((context, token) =>
            {
                context.RegisterCompensation(ct =>
                {
                    Interlocked.Increment(ref compensationInvocations);
                    return ValueTask.CompletedTask;
                });
                return ValueTask.FromResult(Result.Ok(2));
            })
        };

        var result = await Result.WhenAll(operations, policy: policy, cancellationToken: cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        compensationInvocations.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ResultSaga_ShouldReturnState_OnSuccess()
    {
        var saga = new ResultSagaBuilder()
            .AddStep("reserve", static (_, _) => ValueTask.FromResult(Result.Ok("reserveId")))
            .AddStep("charge", static (context, _) =>
            {
                var reserveId = context.State.TryGet("reserve", out string? id) ? id : string.Empty;
                return ValueTask.FromResult(Result.Ok(reserveId + "-charged"));
            }, resultKey: "chargeResult");

        var result = await saga.ExecuteAsync(ResultExecutionPolicy.None, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TryGet("chargeResult", out string? charge).ShouldBeTrue();
        charge.ShouldBe("reserveId-charged");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask StreamingExtensions_ShouldBridgeChannelsAndEnumerables()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();
        var data = GetSequence(TestContext.Current.CancellationToken);

        await data.ToChannelAsync(channel.Writer, TestContext.Current.CancellationToken);

        var collected = new List<int>();
        await foreach (var item in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            item.IsSuccess.ShouldBeTrue();
            collected.Add(item.Value);
        }

        collected.ShouldBe(new[] { 1, 2, 3 });

        static async IAsyncEnumerable<Result<int>> GetSequence([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Delay(10, ct);
            yield return Result.Ok(2);
            yield return Result.Ok(3);
        }
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanInAsync_ShouldMergeSourcesAndCompleteWriter()
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

        await Result.FanInAsync([Source(0, TestContext.Current.CancellationToken), Source(2, TestContext.Current.CancellationToken)], channel.Writer, TestContext.Current.CancellationToken);

        var collected = new List<int>();
        await foreach (var result in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            result.IsSuccess.ShouldBeTrue();
            collected.Add(result.Value);
        }

        collected.Sort();
        collected.ShouldBe([0, 1, 2, 3]);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanInAsync_ShouldPropagateSourceFailure()
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

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Result.FanInAsync([Faulty(TestContext.Current.CancellationToken), Healthy(TestContext.Current.CancellationToken)], channel.Writer, TestContext.Current.CancellationToken));

        var buffered = new List<Result<int>>();
        while (channel.Reader.TryRead(out var item))
        {
            buffered.Add(item);
        }

        buffered.ShouldContain(static result => result.IsSuccess && result.Value == 1);
        channel.Reader.Completion.IsFaulted.ShouldBeTrue();
        var completionException = await Should.ThrowAsync<InvalidOperationException>(async () => await channel.Reader.Completion);
        completionException.ShouldBeSameAs(exception);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanOutAsync_ShouldBroadcastResultsToAllWriters()
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

        firstValues.ShouldBe([3, 4]);
        secondValues.ShouldBe([3, 4]);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask PartitionAsync_ShouldSplitResultsAndCompleteWriters()
    {
        var source = GetSequence();
        var evenWriter = Channel.CreateUnbounded<Result<int>>();
        var oddWriter = Channel.CreateUnbounded<Result<int>>();

        await source.PartitionAsync(static value => value % 2 == 0, evenWriter.Writer, oddWriter.Writer, TestContext.Current.CancellationToken);

        var evenResults = await ReadAllResultsAsync(evenWriter.Reader);
        var oddResults = await ReadAllResultsAsync(oddWriter.Reader);

        evenResults.ShouldAllBe(static result => result.IsSuccess);
        evenResults.Select(static result => result.Value).ShouldBe(new[] { 2 });

        oddResults.Count.ShouldBe(2);
        oddResults[0].IsSuccess.ShouldBeTrue();
        oddResults[0].Value.ShouldBe(1);
        oddResults[1].IsFailure.ShouldBeTrue();

        static async IAsyncEnumerable<Result<int>> GetSequence()
        {
            yield return Result.Ok(1);
            yield return Result.Ok(2);
            yield return Result.Fail<int>(Error.From("failure"));
        }
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ToChannelAsync_ShouldEmitCanceledSentinelWhenEnumerationCanceled()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();
        using var cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);

        var toChannelTask = Sequence(linkedCts.Token).ToChannelAsync(channel.Writer, linkedCts.Token).AsTask();

        var first = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        first.IsSuccess.ShouldBeTrue();

        cts.Cancel();

        await toChannelTask;

        var sentinel = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        sentinel.IsFailure.ShouldBeTrue();
        sentinel.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        await channel.Reader.Completion;

        static async IAsyncEnumerable<Result<int>> Sequence([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            yield return Result.Ok(2);
        }
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WindowAsync_ShouldBatchValues()
    {
        var source = GetSequence(TestContext.Current.CancellationToken);
        var batches = new List<IReadOnlyList<int>>();
        await foreach (var batch in source.WindowAsync(2, TestContext.Current.CancellationToken))
        {
            batch.IsSuccess.ShouldBeTrue();
            batches.Add(batch.Value);
        }

        batches.Count.ShouldBe(2);
        batches[0].ShouldBe([1, 2]);
        batches[1].ShouldBe([3]);

        static async IAsyncEnumerable<Result<int>> GetSequence([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Delay(5, ct);
            yield return Result.Ok(2);
            yield return Result.Ok(3);
        }
    }

    [Fact(Timeout = 15_000)]
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
        grouped.IsSuccess.ShouldBeTrue();
        grouped.Value.Count.ShouldBe(2);

        var partitioned = Result.Partition(data, static value => value % 2 == 0);
        partitioned.IsSuccess.ShouldBeTrue();
        partitioned.Value.True.ShouldBe([2, 4]);
        partitioned.Value.False.ShouldBe([1, 3]);

        var windowed = Result.Window(data, 3);
        windowed.IsSuccess.ShouldBeTrue();
        windowed.Value.Count.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public void HigherOrderOperators_ShouldSurfaceFailure()
    {
        var error = Error.From("fail");
        var data = new[]
        {
            Result.Ok(1),
            Result.Fail<int>(error)
        };

        var grouped = Result.Group(data, value => value);
        grouped.IsFailure.ShouldBeTrue();
        grouped.Error.ShouldBeSameAs(error);

        var partitioned = Result.Partition(data, value => value > 0);
        partitioned.IsFailure.ShouldBeTrue();
        partitioned.Error.ShouldBeSameAs(error);

        var windowed = Result.Window(data, 2);
        windowed.IsFailure.ShouldBeTrue();
        windowed.Error.ShouldBeSameAs(error);

        Should.Throw<ArgumentOutOfRangeException>(() => Result.Window(data, 0));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RetryWithPolicyAsync_ShouldRetryUntilSuccess()
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
        }, policy, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        attempts.ShouldBe(3);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RetryWithPolicyAsync_ShouldReturnFailureAfterExhaustingRetries()
    {
        var attempts = 0;
        var policy = new ResultExecutionPolicy(ResultRetryPolicy.FixedDelay(3, TimeSpan.Zero), Compensation: ResultCompensationPolicy.SequentialReverse);

        var result = await Result.RetryWithPolicyAsync((_, _) =>
        {
            attempts++;
            return ValueTask.FromResult(Result.Fail<int>(Error.From("still failing", ErrorCodes.Exception)));
        }, policy, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        attempts.ShouldBe(3);
        result.Error?.Message.ShouldBe("still failing");
    }

    private static async Task<int[]> ReadAllValuesAsync(ChannelReader<Result<int>> reader)
    {
        var values = new List<int>();
        await foreach (var result in reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            result.IsSuccess.ShouldBeTrue();
            values.Add(result.Value);
        }

        return [.. values];
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
