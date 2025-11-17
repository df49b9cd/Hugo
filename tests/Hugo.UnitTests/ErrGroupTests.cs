using System.Reflection;
using Shouldly;

using Hugo.Policies;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ErrGroupTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldReturnSuccess_WhenAllOperationsComplete()
    {
        using var group = new ErrGroup();
        var counter = 0;

        group.Go(AsValueTask(async ct =>
        {
            await Task.Yield();
            Interlocked.Increment(ref counter);
            return Result.Ok(Unit.Value);
        }));

        group.Go(() =>
        {
            Interlocked.Increment(ref counter);
            return Task.CompletedTask;
        });

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        counter.ShouldBe(2);
        group.Error.ShouldBeNull();
        group.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldReturnFirstError_AndCancelRemainingOperations()
    {
        using var group = new ErrGroup();
        var enteredSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        group.Go(AsValueTask(async ct =>
        {
            await enteredSecond.Task;
            return Result.Fail<Unit>(Error.From("boom", ErrorCodes.Exception));
        }));

        group.Go(AsValueTask(async ct =>
        {
            enteredSecond.TrySetResult(true);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                cancellationObserved.TrySetResult(false);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
            }

            return Result.Ok(Unit.Value);
        }));

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldBe("boom");
        result.Error?.Message.ShouldBe(group.Error?.Message);
        group.Token.IsCancellationRequested.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        (await cancellationObserved.Task).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldSurfaceExceptionsAsStructuredErrors()
    {
        using var group = new ErrGroup();

        group.Go(AsValueTask(static async ct =>
        {
            await Task.Yield();
            throw new InvalidOperationException("explode");
        }));

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldBe("explode");
        group.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldReturnCanceled_WhenLinkedTokenCancels()
    {
        using var externalCts = new CancellationTokenSource();
        using var group = new ErrGroup(externalCts.Token);

        group.Go(AsValueTask(static async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Result.Ok(Unit.Value);
        }));

        await externalCts.CancelAsync();

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        group.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Go_WithCancellationAwareTask_ShouldCompleteSuccessfully()
    {
        using var group = new ErrGroup();
        var observedToken = default(CancellationToken);

        group.Go(async ct =>
        {
            observedToken = ct;
            await Task.Yield();
        });

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        observedToken.CanBeCanceled.ShouldBeTrue();
        observedToken.ShouldBe(group.Token);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Go_WithoutTokenFunction_ShouldRunSuccessfully()
    {
        using var group = new ErrGroup();
        var counter = 0;

        group.Go(AsValueTask(async _ =>
        {
            await Task.Yield();
            Interlocked.Increment(ref counter);
            return Result.Ok(Unit.Value);
        }));

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        counter.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Go_Action_ShouldCaptureExceptionsAsErrors()
    {
        using var group = new ErrGroup();

        group.Go(static () => throw new InvalidOperationException("action failed"));

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldContain("action failed");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Cancel_ShouldCancelRunningWork()
    {
        using var group = new ErrGroup();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        group.Go(async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        await started.Task;
        group.Cancel();
        group.Error.ShouldNotBeNull();
        group.Error?.Code.ShouldBe(ErrorCodes.Canceled);

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        group.Token.IsCancellationRequested.ShouldBeTrue();
        result.Error.ShouldBeSameAs(group.Error);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldReturnCanceled_WhenWaitTokenCanceled()
    {
        using var group = new ErrGroup();
        using var cts = new CancellationTokenSource();

        group.Go(AsValueTask(static async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result.Ok(Unit.Value);
        }));

        var waitTask = group.WaitAsync(cts.Token);
        await cts.CancelAsync();

        var result = await waitTask;

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);

        group.Cancel();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Cancel_ShouldSurfaceCanceledResult_WhenNoWorkStarted()
    {
        using var group = new ErrGroup();

        group.Cancel();
        group.Error.ShouldNotBeNull();
        group.Error?.Code.ShouldBe(ErrorCodes.Canceled);

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        group.Token.IsCancellationRequested.ShouldBeTrue();
        result.Error.ShouldBeSameAs(group.Error);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Cancel_ShouldNotOverwriteExistingError()
    {
        using var group = new ErrGroup();
        var failureRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        group.Go(() =>
        {
            failureRecorded.TrySetResult();
            throw new InvalidOperationException("boom");
        });

        await failureRecorded.Task;
        SpinWait.SpinUntil(() => group.Error is not null, TimeSpan.FromSeconds(1));
        group.Cancel();
        group.Error?.Code.ShouldBe(ErrorCodes.Exception);
        group.Error?.Message.ShouldBe("boom");

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldBe("boom");
        result.Error.ShouldBeSameAs(group.Error);
    }

    [Theory]
    [MemberData(nameof(DisposedGoOverloads))]
    public void Go_ShouldThrowObjectDisposedException_WhenDisposed(Action<ErrGroup> register)
    {
        var group = new ErrGroup();
        group.Dispose();

        var exception = Should.Throw<ObjectDisposedException>(() => register(group));

        exception.ObjectName.ShouldBe(typeof(ErrGroup).FullName);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Go_ShouldNotRunWork_WhenDisposeRacesWithRegistration()
    {
        var group = new ErrGroup();
        using var ready = new ManualResetEventSlim(false);
        using var disposed = new ManualResetEventSlim(false);
        var executed = 0;
        var cancellationToken = TestContext.Current.CancellationToken;

        var registration = Task.Run(() =>
        {
            ready.Set();
            disposed.Wait(cancellationToken);
            return Should.Throw<ObjectDisposedException>(() => group.Go(() =>
            {
                Interlocked.Increment(ref executed);
            }));
        }, cancellationToken);

        var tearDown = Task.Run(() =>
        {
            ready.Wait(cancellationToken);
            group.Dispose();
            disposed.Set();
        }, cancellationToken);

        var exception = await registration;
        await tearDown;

        exception.ObjectName.ShouldBe(typeof(ErrGroup).FullName);
        Volatile.Read(ref executed).ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Token_ShouldRemainAwaitableAfterDispose()
    {
        var group = new ErrGroup();
        var token = group.Token;
        group.Dispose();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingTask = Task.Run(async () =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }, TestContext.Current.CancellationToken);

        await started.Task;
        group.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => waitingTask);
    }

    public static IEnumerable<object[]> DisposedGoOverloads()
    {
        yield return new object[] { new Action<ErrGroup>(group => group.Go(static ct => ValueTask.FromResult(Result.Ok(Unit.Value)))) };
        yield return new object[] { new Action<ErrGroup>(group => group.Go(static ct => Task.CompletedTask)) };
        yield return new object[] { new Action<ErrGroup>(group => group.Go(static () => ValueTask.FromResult(Result.Ok(Unit.Value)))) };
        yield return new object[] { new Action<ErrGroup>(group => group.Go(static () => Task.CompletedTask)) };
        yield return new object[] { new Action<ErrGroup>(group => group.Go(static () => { })) };
        yield return new object[]
        {
            new Action<ErrGroup>(group => group.Go(
                static (ctx, ct) => ValueTask.FromResult(Result.Ok(Unit.Value)),
                stepName: "test",
                policy: Hugo.Policies.ResultExecutionPolicy.None,
                timeProvider: TimeProvider.System))
        };
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask PipelineFailure_ShouldCancelPeersBeforeCompensation()
    {
        using var group = new ErrGroup();
        var peerCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var compensationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompensationToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulatedCompensationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        group.Go((ctx, ct) =>
        {
            _ = Task.Run(async () =>
            {
                compensationStarted.TrySetResult();
                try
                {
                    await allowCompensationToFinish.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Group cancellation should unblock peer work even if compensation is still running.
                }
                finally
                {
                    simulatedCompensationCompleted.TrySetResult();
                }
            });

            return ValueTask.FromResult(Result.Fail<Unit>(Error.From("pipeline failed", ErrorCodes.Exception)));
        }, timeProvider: TimeProvider.System, policy: ResultExecutionPolicy.None);

        group.Go(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                peerCanceled.TrySetResult(true);
            }
        });

        await compensationStarted.Task;
        var completed = await Task.WhenAny(peerCanceled.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        completed.ShouldBeSameAs(peerCanceled.Task);
        allowCompensationToFinish.Task.IsCompleted.ShouldBeFalse();

        allowCompensationToFinish.TrySetResult();
        await simulatedCompensationCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public void Go_Pipeline_ShouldAggregateCompensationFailures()
    {
        var group = new ErrGroup();
        var failure = Error.From("pipeline failed", ErrorCodes.Exception);
        var aggregated = Error.Aggregate("ErrGroup step failed with compensation error.", failure, Error.From("compensation failed", ErrorCodes.Exception));

        var trySetError = typeof(ErrGroup).GetMethod("TrySetError", BindingFlags.Instance | BindingFlags.NonPublic);
        trySetError.ShouldNotBeNull();
        var setResult = (bool?)trySetError!.Invoke(group, [failure]);
        (setResult ?? false).ShouldBeTrue();

        var updateError = typeof(ErrGroup).GetMethod("UpdateError", BindingFlags.Instance | BindingFlags.NonPublic);
        updateError.ShouldNotBeNull();
        updateError!.Invoke(group, [failure, aggregated]);

        var errorField = typeof(ErrGroup).GetField("_error", BindingFlags.Instance | BindingFlags.NonPublic);
        var recorded = (Error?)errorField?.GetValue(group);

        recorded.ShouldBe(aggregated);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Go_Pipeline_ShouldExecuteCompensationPolicyOncePerScope()
    {
        var scope = new CompensationScope();
        scope.Register(static _ => ValueTask.CompletedTask);
        var invocationCount = 0;
        var policy = ResultExecutionPolicy.None.WithCompensation(new ResultCompensationPolicy(context =>
        {
            Interlocked.Increment(ref invocationCount);
            return context.ExecuteAsync();
        }));

        var error = await Result.RunCompensationAsync(policy, scope, CancellationToken.None);

        error.ShouldBeNull();
        invocationCount.ShouldBe(1);
    }

    private static Func<CancellationToken, ValueTask<Result<Unit>>> AsValueTask(Func<CancellationToken, Task<Result<Unit>>> work) =>
        ct => new ValueTask<Result<Unit>>(work(ct));
}
