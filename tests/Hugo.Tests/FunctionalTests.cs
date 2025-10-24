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
    public void Then_ShouldThrow_WhenNextIsNull() => Assert.Throws<ArgumentNullException>(() => Ok(1).Then((Func<int, Result<int>>)null!));

    [Fact]
    public void Map_ShouldThrow_WhenMapperIsNull() => Assert.Throws<ArgumentNullException>(() => Ok(1).Map((Func<int, int>)null!));

    [Fact]
    public void Tap_ShouldThrow_WhenActionIsNull() => Assert.Throws<ArgumentNullException>(() => Ok(1).Tap(null!));

    [Fact]
    public void Error_From_ShouldThrow_WhenMessageIsNull() => Assert.Throws<ArgumentNullException>(() => Error.From(null!));

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
            .Then(v => Ok(v * 5))
            .Then(v => Ok(v.ToString()));

        Assert.True(result.IsSuccess);
        Assert.Equal("10", result.Value);
    }

    [Fact]
    public void Then_ShouldBypassNext_OnFailure()
    {
        var error = Error.From("oops");
        var result = Err<int>(error).Then(v => Ok(v * 2));

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task ThenAsync_ShouldComposeSyncBinder_WithAsyncResult()
    {
        var result = await Task.FromResult(Ok("start"))
            .ThenAsync(value => Ok(value + "-next"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("start-next", result.Value);
    }

    [Fact]
    public async Task ThenAsync_ShouldComposeAsyncBinder()
    {
        var result = await Ok("start")
            .ThenAsync(
                (value, _) => Task.FromResult(Ok(value + "-async")),
                TestContext.Current.CancellationToken
            );

        Assert.True(result.IsSuccess);
        Assert.Equal("start-async", result.Value);
    }

    [Fact]
    public void Map_ShouldTransformValue()
    {
        var result = Ok(5).Map(v => v * 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value);
    }

    [Fact]
    public async Task MapAsync_ShouldTransformAsync()
    {
        var result = await Ok(5)
            .MapAsync((value, _) => Task.FromResult(value * 3), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(15, result.Value);
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
    public void Recover_ShouldConvertFailure()
    {
        var recovered = Err<int>("whoops").Recover(err => Ok(err.Message.Length));

        Assert.True(recovered.IsSuccess);
        Assert.Equal(6, recovered.Value);
    }

    [Fact]
    public async Task RecoverAsync_Result_ShouldConvertFailure()
    {
        var recovered = await Err<int>("fail").RecoverAsync(
            (error, _) => Task.FromResult(Ok(error.Message.Length)),
            TestContext.Current.CancellationToken
        );

        Assert.True(recovered.IsSuccess);
        Assert.Equal(4, recovered.Value);
    }

    [Fact]
    public async Task RecoverAsync_ShouldConvertFailure()
    {
        var recovered = await Task.FromResult(Err<int>("fail"))
            .RecoverAsync(
                (err, _) => Task.FromResult(Ok(err.Message.Length)),
                TestContext.Current.CancellationToken
            );

        Assert.True(recovered.IsSuccess);
        Assert.Equal(4, recovered.Value);
    }

    [Fact]
    public void Ensure_ShouldFail_WhenPredicateIsFalse()
    {
        var result = Ok(5).Ensure(v => v > 10);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public async Task EnsureAsync_ShouldFail_WhenPredicateIsFalse()
    {
        var result = await Ok(5).EnsureAsync(
            (v, _) => Task.FromResult(v > 10),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
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
        var success = Ok("value").Finally(v => v.ToUpperInvariant(), err => err.Message);
        var failure = Err<string>("fail").Finally(v => v, err => err.Message);

        Assert.Equal("VALUE", success);
        Assert.Equal("fail", failure);
    }

    [Fact]
    public async Task FinallyAsync_ShouldSelectBranch_ForTask()
    {
        var success = await Task.FromResult(Ok(1))
            .FinallyAsync(
                v => $"{v}-success",
                err => err.Message,
                TestContext.Current.CancellationToken
            );

        var failure = await Task.FromResult(Err<int>("fail"))
            .FinallyAsync(
                v => v.ToString(CultureInfo.InvariantCulture),
                err => err.Message,
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
                (v, _) => Task.FromResult(Ok(v + 1)),
                TestContext.Current.CancellationToken
            )
            .MapAsync(v => v * 5, TestContext.Current.CancellationToken)
            .TapAsync(
                (value, _) =>
                {
                    Assert.Equal(10, value);
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken
            )
            .EnsureAsync(
                (value, _) => Task.FromResult(value == 10),
                TestContext.Current.CancellationToken
            )
            .RecoverAsync(
                _ => Ok(42),
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
                async (_, ct) =>
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
        var result = await task.MapAsync(v => v, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task RecoverAsync_ShouldSkip_WhenCancellationOccurs()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = Task.FromCanceled<Result<int>>(cts.Token);
        var result = await task.RecoverAsync(_ => Ok(5), TestContext.Current.CancellationToken);

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
        var mapped = await resultTask.MapAsync(v => v * 3, TestContext.Current.CancellationToken);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(6, mapped.Value);
    }

    [Fact]
    public async Task MapAsync_TaskResult_WithAsyncMapper_ShouldTransformValue()
    {
        var resultTask = Task.FromResult(Ok(3));
        var mapped = await resultTask.MapAsync(
            async (value, ct) =>
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
        var recovered = await task.RecoverAsync(async (_, ct) =>
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
        var ensured = await task.EnsureAsync(async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return false;
        }, TestContext.Current.CancellationToken);

        Assert.True(ensured.IsFailure);
        Assert.Equal(ErrorCodes.Validation, ensured.Error?.Code);
    }

    [Fact]
    public async Task FinallyAsync_Result_ShouldInvokeAsyncHandlers()
    {
        var result = await Ok(1).FinallyAsync(
            async (value, ct) =>
            {
                await Task.Delay(5, ct);
                return value + 1;
            },
            async (_, ct) =>
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
            async (value, ct) =>
            {
                await Task.Delay(5, ct);
                return value.ToUpperInvariant();
            },
            async (_, ct) =>
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
            async (value, ct) =>
            {
                await Task.Delay(1, ct);
                return value.ToString(CultureInfo.InvariantCulture);
            },
            async (error, ct) =>
            {
                await Task.Delay(1, ct);
                return error.Code ?? string.Empty;
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ErrorCodes.Canceled, result);
    }

    [Fact]
    public void Where_ShouldFailWhenPredicateFalse()
    {
        var result = Ok(1).Where(v => v > 5);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }
}
