using System.Globalization;

using static Hugo.Go;

namespace Hugo.Tests;

public class FunctionalTests
{
    [Fact]
    public void Err_ShouldReturnDefaultError_WhenGivenNull()
    {
        var (_, err) = Err<string>((Error?)null);
        Assert.NotNull(err);
        Assert.Equal("An unspecified error occurred.", err.Message);
    }

    [Fact]
    public void Then_ShouldThrow_WhenNextIsNull() => Assert.Throws<ArgumentNullException>(static () => Ok(1).Then((Func<int, Result<int>>)null!));

    [Fact]
    public void Map_ShouldThrow_WhenMapperIsNull() => Assert.Throws<ArgumentNullException>(static () => Ok(1).Map((Func<int, int>)null!));

    [Fact]
    public void Tap_ShouldThrow_WhenActionIsNull() => Assert.Throws<ArgumentNullException>(static () => Ok(1).Tap(null!));

    [Fact]
    public void Error_From_ShouldThrow_WhenMessageIsNull() => Assert.Throws<ArgumentNullException>(static () => Error.From(null!));

    [Fact]
    public void Error_ShouldImplicitlyConvert_FromString()
    {
        Error? err = "message";
        Assert.NotNull(err);
        Assert.Equal("message", err.Message);
    }

    [Fact]
    public void Error_ShouldImplicitlyConvert_FromException()
    {
        Error? err = new InvalidOperationException("boom");
        Assert.NotNull(err);
        Assert.Equal("boom", err.Message);
        Assert.Equal(ErrorCodes.Exception, err.Code);
    }

    [Fact]
    public void Then_ShouldExecuteNext_OnSuccess()
    {
        var result = Ok(2)
            .Then(static v => Ok(v * 5))
            .Then(static v => Ok(v.ToString()));

        Assert.True(result.IsSuccess);
        Assert.Equal("10", result.Value);
    }

    [Fact]
    public void Then_ShouldBypassNext_OnFailure()
    {
        var error = Error.From("oops");
        var result = Err<int>(error).Then(static v => Ok(v * 2));

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task ThenAsync_ShouldComposeSyncBinder_WithAsyncResult()
    {
        var result = await Task.FromResult(Ok("start"))
            .ThenAsync(static value => Ok(value + "-next"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("start-next", result.Value);
    }

    [Fact]
    public async Task ThenAsync_ShouldComposeAsyncBinder()
    {
        var result = await Ok("start")
            .ThenAsync(
                static (value, _) => Task.FromResult(Ok(value + "-async")),
                TestContext.Current.CancellationToken
            );

        Assert.True(result.IsSuccess);
        Assert.Equal("start-async", result.Value);
    }

    [Fact]
    public async Task ThenAsync_ShouldComposeValueTaskBinder()
    {
        var result = await Ok("start")
            .ThenValueTaskAsync(
                static async ValueTask<Result<string>> (value, token) =>
                {
                    await Task.Delay(1, token);
                    return Result.Ok(value + "-valuetask");
                },
                TestContext.Current.CancellationToken
            );

        Assert.True(result.IsSuccess);
        Assert.Equal("start-valuetask", result.Value);
    }

    [Fact]
    public async Task ThenAsync_ShouldComposeValueTaskResultSource()
    {
        static ValueTask<Result<string>> Stage() => ValueTask.FromResult(Result.Ok("begin"));

        var result = await Stage()
            .ThenAsync(static value => Result.Ok(value + "-done"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("begin-done", result.Value);
    }

    [Fact]
    public void Map_ShouldTransformValue()
    {
        var result = Ok(5).Map(static v => v * 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value);
    }

    [Fact]
    public async Task MapAsync_ShouldTransformAsync()
    {
        var result = await Ok(5)
            .MapAsync(static (value, _) => Task.FromResult(value * 3), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(15, result.Value);
    }

    [Fact]
    public async Task MapAsync_ShouldSupportValueTaskMapper()
    {
        var result = await Ok(7)
            .MapValueTaskAsync(static async ValueTask<int> (value, token) =>
            {
                await Task.Delay(1, token);
                return value * 2;
            }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(14, result.Value);
    }

    [Fact]
    public async Task MapAsync_ShouldSupportValueTaskResultSource()
    {
        var mapped = await ValueTask.FromResult(Result.Ok(3))
            .MapAsync(static value => value + 1, TestContext.Current.CancellationToken);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(4, mapped.Value);
    }

    [Fact]
    public void Tap_ShouldRunSideEffect_WhenSuccessful()
    {
        var tapped = false;
        var result = Ok("value").Tap(_ => tapped = true);

        Assert.True(tapped);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Tap_ShouldSkipSideEffect_WhenFailure()
    {
        var tapped = false;
        var result = Err<string>("error").Tap(_ => tapped = true);

        Assert.False(tapped);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task TapAsync_ShouldSupportAsyncSideEffects()
    {
        var tapped = false;
        var result = await Ok(1)
            .TapAsync(
                async (_, _) =>
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                    tapped = true;
                },
                TestContext.Current.CancellationToken
            );

        Assert.True(tapped);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task TapAsync_ShouldSupportValueTaskSideEffects()
    {
        var callCount = 0;
        var result = await Ok(2)
            .TapValueTaskAsync(
                async ValueTask (value, token) =>
                {
                    await Task.Delay(1, token);
                    callCount += value;
                },
                TestContext.Current.CancellationToken
            );

        Assert.True(result.IsSuccess);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task TapErrorAsync_ShouldSupportValueTaskSideEffects()
    {
        var observed = 0;
        var result = await ValueTask.FromResult(Result.Fail<int>(Error.From("fail", ErrorCodes.Validation)))
            .TapErrorValueTaskAsync(
                async ValueTask (error, token) =>
                {
                    await Task.Delay(1, token);
                    observed = error.Message.Length;
                },
                TestContext.Current.CancellationToken
            );

        Assert.True(result.IsFailure);
        Assert.Equal("fail".Length, observed);
    }

    [Fact]
    public void Recover_ShouldConvertFailure()
    {
        var recovered = Err<int>("whoops").Recover(static err => Ok(err.Message.Length));

        Assert.True(recovered.IsSuccess);
        Assert.Equal(6, recovered.Value);
    }

    [Fact]
    public async Task RecoverAsync_Result_ShouldConvertFailure()
    {
        var recovered = await Err<int>("fail").RecoverAsync(
            static (error, _) => Task.FromResult(Ok(error.Message.Length)),
            TestContext.Current.CancellationToken
        );

        Assert.True(recovered.IsSuccess);
        Assert.Equal(4, recovered.Value);
    }

    [Fact]
    public async Task RecoverAsync_ShouldSupportValueTaskRecover()
    {
        var recovered = await Err<int>("boom").RecoverValueTaskAsync(
            static async ValueTask<Result<int>> (error, token) =>
            {
                await Task.Delay(1, token);
                return Result.Ok(error.Message.Length);
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(recovered.IsSuccess);
        Assert.Equal(4, recovered.Value);
    }

    [Fact]
    public async Task RecoverAsync_ShouldSupportValueTaskResultSource()
    {
        var recovered = await ValueTask.FromResult(Result.Fail<int>(Error.From("oops")))
            .RecoverAsync(static error => Result.Ok(error.Message.Length), TestContext.Current.CancellationToken);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(4, recovered.Value);
    }

    [Fact]
    public async Task RecoverAsync_ShouldConvertFailure()
    {
        var recovered = await Task.FromResult(Err<int>("fail"))
            .RecoverAsync(
                static (err, _) => Task.FromResult(Ok(err.Message.Length)),
                TestContext.Current.CancellationToken
            );

        Assert.True(recovered.IsSuccess);
        Assert.Equal(4, recovered.Value);
    }

    [Fact]
    public void Ensure_ShouldFail_WhenPredicateIsFalse()
    {
        var result = Ok(5).Ensure(static v => v > 10);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public async Task EnsureAsync_ShouldFail_WhenPredicateIsFalse()
    {
        var result = await Ok(5).EnsureAsync(
            static (v, _) => Task.FromResult(v > 10),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public async Task EnsureAsync_ShouldSupportValueTaskPredicate()
    {
        var result = await Ok(42).EnsureValueTaskAsync(
            static async ValueTask<bool> (value, token) =>
            {
                await Task.Delay(1, token);
                return value > 10;
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EnsureAsync_ShouldSupportValueTaskResultSource()
    {
        var evaluation = await ValueTask.FromResult(Result.Ok(1))
            .EnsureValueTaskAsync(static (value, _) => new ValueTask<bool>(value > 10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(evaluation.IsFailure);
        Assert.Equal(ErrorCodes.Validation, evaluation.Error?.Code);
    }

    [Fact]
    public void TapError_ShouldRunSideEffect_WhenFailure()
    {
        Error? tappedError = null;
        var result = Err<int>("fault").TapError(err => tappedError = err);

        Assert.True(result.IsFailure);
        Assert.NotNull(tappedError);
        Assert.Equal("fault", tappedError!.Message);
    }

    [Fact]
    public void Finally_ShouldSelectBranch()
    {
        var success = Ok("value").Finally(static v => v.ToUpperInvariant(), static err => err.Message);
        var failure = Err<string>("fail").Finally(static v => v, static err => err.Message);

        Assert.Equal("VALUE", success);
        Assert.Equal("fail", failure);
    }

    [Fact]
    public async Task FinallyAsync_ShouldSelectBranch_ForTask()
    {
        var success = await Task.FromResult(Ok(1))
            .FinallyAsync(
                static v => $"{v}-success",
                static err => err.Message,
                TestContext.Current.CancellationToken
            );

        var failure = await Task.FromResult(Err<int>("fail"))
            .FinallyAsync(
                static v => v.ToString(CultureInfo.InvariantCulture),
                static err => err.Message,
                TestContext.Current.CancellationToken
            );

        Assert.Equal("1-success", success);
        Assert.Equal("fail", failure);
    }

    [Fact]
    public void Linq_Query_Comprehension_ShouldWork()
    {
        var query =
            from a in Ok(2)
            from b in Ok(3)
            select a * b;

        Assert.True(query.IsSuccess);
        Assert.Equal(6, query.Value);
    }

    [Fact]
    public async Task Pipeline_ShouldHandleMixedOperations()
    {
        var result = await Ok(1)
            .ThenAsync(
                static (v, _) => Task.FromResult(Ok(v + 1)),
                TestContext.Current.CancellationToken
            )
            .MapAsync(static v => v * 5, TestContext.Current.CancellationToken)
            .TapAsync(
                static (value, _) =>
                {
                    Assert.Equal(10, value);
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken
            )
            .EnsureAsync(
                static (value, _) => Task.FromResult(value == 10),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .RecoverAsync(
                static _ => Ok(42),
                TestContext.Current.CancellationToken
            );

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value);
    }

    [Fact]
    public async Task Cancellation_ShouldPropagateThroughChain()
    {
        using var cts = new CancellationTokenSource(50);

        var result = await Ok("start")
            .ThenAsync(
                static async (_, ct) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return Ok("never");
                },
                cts.Token
            );

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task MapAsync_ShouldReturnCancellationError_WhenTaskCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = Task.FromCanceled<Result<int>>(cts.Token);
        var result = await task.MapAsync(static v => v, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task RecoverAsync_ShouldSkip_WhenCancellationOccurs()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = Task.FromCanceled<Result<int>>(cts.Token);
        var result = await task.RecoverAsync(static _ => Ok(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public void OnSuccess_ShouldInvokeAction()
    {
        var invoked = false;
        _ = Ok(1).OnSuccess(_ => invoked = true);

        Assert.True(invoked);
    }

    [Fact]
    public async Task OnSuccessAsync_Result_ShouldInvokeAction()
    {
        var invoked = false;
        var result = await Ok(2).OnSuccessAsync(
            async (value, ct) =>
            {
                await Task.Delay(5, ct);
                invoked = value == 2;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.True(invoked);
    }

    [Fact]
    public async Task OnSuccessAsync_TaskResult_WithAsyncAction_ShouldInvoke()
    {
        var invoked = false;
        var result = await Task.FromResult(Ok(3)).OnSuccessAsync(
            async (value, ct) =>
            {
                await Task.Delay(5, ct);
                invoked = value == 3;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.True(invoked);
    }

    [Fact]
    public async Task OnSuccessAsync_ShouldSupportValueTaskAction()
    {
        var invoked = false;
        var result = await Ok(9).OnSuccessValueTaskAsync(
            async ValueTask (value, token) =>
            {
                await Task.Delay(1, token);
                invoked = value == 9;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.True(invoked);
    }

    [Fact]
    public async Task OnFailureAsync_ShouldSupportValueTaskAction()
    {
        Error? observed = null;
        var result = await Err<int>("fail").OnFailureValueTaskAsync(
            async ValueTask (error, token) =>
            {
                await Task.Delay(1, token);
                observed = error;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsFailure);
        Assert.NotNull(observed);
    }

    [Fact]
    public void OnFailure_ShouldInvokeAction()
    {
        Error? captured = null;
        _ = Err<int>("fail").OnFailure(err => captured = err);

        Assert.NotNull(captured);
    }

    [Fact]
    public async Task OnFailureAsync_Result_ShouldInvokeAction()
    {
        Error? captured = null;
        var result = await Err<int>("fail").OnFailureAsync(
            async (error, ct) =>
            {
                await Task.Delay(5, ct);
                captured = error;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsFailure);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task OnFailureAsync_TaskResult_WithAsyncAction_ShouldInvoke()
    {
        Error? captured = null;
        var result = await Task.FromResult(Err<int>("fail")).OnFailureAsync(
            async (error, ct) =>
            {
                await Task.Delay(5, ct);
                captured = error;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsFailure);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task TapErrorAsync_TaskResult_WithAction_ShouldInvoke()
    {
        Error? captured = null;
        var result = await Task.FromResult(Err<int>("fail")).TapErrorAsync(
            error => captured = error,
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsFailure);
        Assert.NotNull(captured);
    }

    [Fact]
    public void Tee_ShouldBehaveLikeTap()
    {
        var tapped = false;
        var result = Ok(3).Tee(_ => tapped = true);

        Assert.True(result.IsSuccess);
        Assert.True(tapped);
    }

    [Fact]
    public async Task TeeAsync_TaskResult_WithAction_ShouldInvoke()
    {
        var tapped = false;
        var resultTask = Task.FromResult(Ok("value"));
        var result = await resultTask.TeeAsync(_ => tapped = true, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(tapped);
    }

    [Fact]
    public async Task TeeAsync_TaskResult_WithAsyncSideEffect_ShouldInvoke()
    {
        var tapped = false;
        var resultTask = Task.FromResult(Ok(5));
        var result = await resultTask.TeeAsync(async (_, ct) =>
        {
            await Task.Delay(5, ct);
            tapped = true;
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(tapped);
    }

    [Fact]
    public async Task MapAsync_TaskResult_ShouldTransformValue()
    {
        var resultTask = Task.FromResult(Ok(2));
        var mapped = await resultTask.MapAsync(static v => v * 3, TestContext.Current.CancellationToken);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(6, mapped.Value);
    }

    [Fact]
    public async Task MapAsync_TaskResult_WithAsyncMapper_ShouldTransformValue()
    {
        var resultTask = Task.FromResult(Ok(3));
        var mapped = await resultTask.MapAsync(
            static async (value, ct) =>
            {
                await Task.Delay(5, ct);
                return value * 2;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(mapped.IsSuccess);
        Assert.Equal(6, mapped.Value);
    }

    [Fact]
    public async Task RecoverAsync_TaskResultWithAsyncRecover_ShouldReturnRecoveredResult()
    {
        var task = Task.FromResult(Err<int>("fail"));
        var recovered = await task.RecoverAsync(static async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return Ok(9);
        }, TestContext.Current.CancellationToken);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(9, recovered.Value);
    }

    [Fact]
    public async Task EnsureAsync_TaskResult_ShouldFailWhenPredicateFalse()
    {
        var task = Task.FromResult(Ok(1));
        var ensured = await task.EnsureAsync(static async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return false;
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(ensured.IsFailure);
        Assert.Equal(ErrorCodes.Validation, ensured.Error?.Code);
    }

    [Fact]
    public async Task FinallyAsync_Result_ShouldInvokeAsyncHandlers()
    {
        var result = await Ok(1).FinallyAsync(
            static async (value, ct) =>
            {
                await Task.Delay(5, ct);
                return value + 1;
            },
            static async (_, ct) =>
            {
                await Task.Delay(5, ct);
                return -1;
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, result);
    }

    [Fact]
    public async Task FinallyAsync_TaskResult_ShouldInvokeAsyncHandlers()
    {
        var outcome = await Task.FromResult(Ok("value")).FinallyAsync(
            static async (value, ct) =>
            {
                await Task.Delay(5, ct);
                return value.ToUpperInvariant();
            },
            static async (_, ct) =>
            {
                await Task.Delay(5, ct);
                return string.Empty;
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("VALUE", outcome);
    }

    [Fact]
    public async Task FinallyAsync_TaskResult_ShouldReturnCanceledError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = Task.FromCanceled<Result<int>>(cts.Token);
        var result = await task.FinallyAsync(
            static async (value, ct) =>
            {
                await Task.Delay(1, ct);
                return value.ToString(CultureInfo.InvariantCulture);
            },
            static async (error, ct) =>
            {
                await Task.Delay(1, ct);
                return error.Code ?? string.Empty;
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ErrorCodes.Canceled, result);
    }

    [Fact]
    public async Task FinallyAsync_ValueTaskResult_ShouldSupportValueTaskContinuations()
    {
        var outcome = await ValueTask.FromResult(Result.Ok("value"))
            .FinallyValueTaskAsync(
                async ValueTask<string> (value, token) =>
                {
                    await Task.Delay(1, token);
                    return value + "-ok";
                },
                async ValueTask<string> (error, token) =>
                {
                    await Task.Delay(1, token);
                    return error.Message;
                },
                TestContext.Current.CancellationToken
            );

        Assert.Equal("value-ok", outcome);
    }

    [Fact]
    public void Where_ShouldFailWhenPredicateFalse()
    {
        var result = Ok(1).Where(static v => v > 5);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }
}
