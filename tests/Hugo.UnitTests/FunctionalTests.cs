using System.Diagnostics.Metrics;
using System.Globalization;

using Shouldly;

using static Hugo.Go;

namespace Hugo.Tests;

[Collection(TestCollections.DiagnosticsIsolation)]
public class FunctionalTests
{
    [Fact(Timeout = 15_000)]
    public void Err_ShouldReturnDefaultError_WhenGivenNull()
    {
        var (_, err) = Err<string>((Error?)null);
        err.ShouldNotBeNull();
        err.Message.ShouldBe("An unspecified error occurred.");
    }

    [Fact(Timeout = 15_000)]
    public void Then_ShouldThrow_WhenNextIsNull() => Should.Throw<ArgumentNullException>(static () => Ok(1).Then((Func<int, Result<int>>)null!));

    [Fact(Timeout = 15_000)]
    public void Map_ShouldThrow_WhenMapperIsNull() => Should.Throw<ArgumentNullException>(static () => Ok(1).Map((Func<int, int>)null!));

    [Fact(Timeout = 15_000)]
    public void Tap_ShouldThrow_WhenActionIsNull() => Should.Throw<ArgumentNullException>(static () => Ok(1).Tap(null!));

    [Fact(Timeout = 15_000)]
    public void Error_From_ShouldThrow_WhenMessageIsNull() => Should.Throw<ArgumentNullException>(static () => Error.From(null!));

    [Fact(Timeout = 15_000)]
    public void Error_ShouldImplicitlyConvert_FromString()
    {
        Error? err = "message";
        err.ShouldNotBeNull();
        err.Message.ShouldBe("message");
    }

    [Fact(Timeout = 15_000)]
    public void Error_ShouldImplicitlyConvert_FromException()
    {
        Error? err = new InvalidOperationException("boom");
        err.ShouldNotBeNull();
        err.Message.ShouldBe("boom");
        err.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public void Then_ShouldExecuteNext_OnSuccess()
    {
        var result = Ok(2)
            .Then(static v => Ok(v * 5))
            .Then(static v => Ok(v.ToString()));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("10");
    }

    [Fact(Timeout = 15_000)]
    public void Then_ShouldBypassNext_OnFailure()
    {
        var error = Error.From("oops");
        var result = Err<int>(error).Then(static v => Ok(v * 2));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeSameAs(error);
    }

    [Fact(Timeout = 15_000)]
    public void Then_ShouldNotIncrementDiagnostics_OnFailurePropagation()
    {
        var error = Error.From("boom", ErrorCodes.Exception);
        var (failures, propagated) = CaptureFailureDiagnostics(() => Err<int>(error).Then(static _ => Ok(42)));

        propagated.IsFailure.ShouldBeTrue();
        propagated.Error.ShouldBeSameAs(error);
        failures.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public void SelectMany_ShouldReuseOriginalError()
    {
        var error = Error.From("boom", ErrorCodes.Exception);
        var (failures, propagated) = CaptureFailureDiagnostics(() =>
            Err<int>(error).SelectMany(static _ => Ok("ignored"), static (_, projection) => projection));

        propagated.IsFailure.ShouldBeTrue();
        propagated.Error.ShouldBeSameAs(error);
        failures.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ThenAsync_ShouldComposeSyncBinder_WithAsyncResult()
    {
        var result = await ValueTask.FromResult(Ok("start"))
            .ThenAsync(static value => Ok(value + "-next"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("start-next");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ThenAsync_ShouldComposeAsyncBinder()
    {
        var result = await Ok("start")
            .ThenAsync(
                static (value, _) => ValueTask.FromResult(Ok(value + "-async")),
                TestContext.Current.CancellationToken
            );

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("start-async");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ThenAsync_ShouldComposeValueTaskBinder()
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("start-valuetask");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ThenAsync_ShouldComposeValueTaskResultSource()
    {
        static ValueTask<Result<string>> Stage() => ValueTask.FromResult(Result.Ok("begin"));

        var result = await Stage()
            .ThenAsync(static value => Result.Ok(value + "-done"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("begin-done");
    }

    [Fact(Timeout = 15_000)]
    public void Map_ShouldTransformValue()
    {
        var result = Ok(5).Map(static v => v * 2);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(10);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapAsync_ShouldTransformAsync()
    {
        var result = await Ok(5)
            .MapAsync(static (value, _) => ValueTask.FromResult(value * 3), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(15);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapAsync_ShouldSupportValueTaskMapper()
    {
        var result = await Ok(7)
            .MapValueTaskAsync(static async ValueTask<int> (value, token) =>
            {
                await Task.Delay(1, token);
                return value * 2;
            }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(14);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapAsync_ShouldSupportValueTaskResultSource()
    {
        var mapped = await ValueTask.FromResult(Result.Ok(3))
            .MapAsync(static value => value + 1, TestContext.Current.CancellationToken);

        mapped.IsSuccess.ShouldBeTrue();
        mapped.Value.ShouldBe(4);
    }

    [Fact(Timeout = 15_000)]
    public void Tap_ShouldRunSideEffect_WhenSuccessful()
    {
        var tapped = false;
        var result = Ok("value").Tap(_ => tapped = true);

        tapped.ShouldBeTrue();
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public void Tap_ShouldSkipSideEffect_WhenFailure()
    {
        var tapped = false;
        var result = Err<string>("error").Tap(_ => tapped = true);

        tapped.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapAsync_ShouldSupportAsyncSideEffects()
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

        tapped.ShouldBeTrue();
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapAsync_ShouldSupportValueTaskSideEffects()
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

        result.IsSuccess.ShouldBeTrue();
        callCount.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapErrorAsync_ShouldSupportValueTaskSideEffects()
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

        result.IsFailure.ShouldBeTrue();
        observed.ShouldBe("fail".Length);
    }

    [Fact(Timeout = 15_000)]
    public void Recover_ShouldConvertFailure()
    {
        var recovered = Err<int>("whoops").Recover(static err => Ok(err.Message.Length));

        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value.ShouldBe(6);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RecoverAsync_Result_ShouldConvertFailure()
    {
        var recovered = await Err<int>("fail").RecoverAsync(
            static (error, _) => ValueTask.FromResult(Ok(error.Message.Length)),
            TestContext.Current.CancellationToken
        );

        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value.ShouldBe(4);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RecoverAsync_ShouldSupportValueTaskRecover()
    {
        var recovered = await Err<int>("boom").RecoverValueTaskAsync(
            static async ValueTask<Result<int>> (error, token) =>
            {
                await Task.Delay(1, token);
                return Result.Ok(error.Message.Length);
            },
            TestContext.Current.CancellationToken
        );

        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value.ShouldBe(4);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RecoverAsync_ShouldSupportValueTaskResultSource()
    {
        var recovered = await ValueTask.FromResult(Result.Fail<int>(Error.From("oops")))
            .RecoverAsync(static error => Result.Ok(error.Message.Length), TestContext.Current.CancellationToken);

        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value.ShouldBe(4);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RecoverAsync_ShouldConvertFailure()
    {
        var recovered = await ValueTask.FromResult(Err<int>("fail"))
            .RecoverAsync(
                static (err, _) => ValueTask.FromResult(Ok(err.Message.Length)),
                TestContext.Current.CancellationToken
            );

        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value.ShouldBe(4);
    }

    [Fact(Timeout = 15_000)]
    public void Ensure_ShouldFail_WhenPredicateIsFalse()
    {
        var result = Ok(5).Ensure(static v => v > 10);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask EnsureAsync_ShouldFail_WhenPredicateIsFalse()
    {
        var result = await Ok(5).EnsureAsync(
            static (v, _) => ValueTask.FromResult(v > 10),
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask EnsureAsync_ShouldSupportValueTaskPredicate()
    {
        var result = await Ok(42).EnsureValueTaskAsync(
            static async ValueTask<bool> (value, token) =>
            {
                await Task.Delay(1, token);
                return value > 10;
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask EnsureAsync_ShouldSupportValueTaskResultSource()
    {
        var evaluation = await ValueTask.FromResult(Result.Ok(1))
            .EnsureValueTaskAsync(static (value, _) => new ValueTask<bool>(value > 10), cancellationToken: TestContext.Current.CancellationToken);

        evaluation.IsFailure.ShouldBeTrue();
        evaluation.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public void TapError_ShouldRunSideEffect_WhenFailure()
    {
        Error? tappedError = null;
        var result = Err<int>("fault").TapError(err => tappedError = err);

        result.IsFailure.ShouldBeTrue();
        tappedError.ShouldNotBeNull();
        tappedError!.Message.ShouldBe("fault");
    }

    [Fact(Timeout = 15_000)]
    public void Finally_ShouldSelectBranch()
    {
        var success = Ok("value").Finally(static v => v.ToUpperInvariant(), static err => err.Message);
        var failure = Err<string>("fail").Finally(static v => v, static err => err.Message);

        success.ShouldBe("VALUE");
        failure.ShouldBe("fail");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FinallyAsync_ShouldSelectBranch_ForTask()
    {
        var success = await ValueTask.FromResult(Ok(1))
            .FinallyAsync(
                static v => $"{v}-success",
                static err => err.Message,
                TestContext.Current.CancellationToken
            );

        var failure = await ValueTask.FromResult(Err<int>("fail"))
            .FinallyAsync(
                static v => v.ToString(CultureInfo.InvariantCulture),
                static err => err.Message,
                TestContext.Current.CancellationToken
            );

        success.ShouldBe("1-success");
        failure.ShouldBe("fail");
    }

    [Fact(Timeout = 15_000)]
    public void Linq_Query_Comprehension_ShouldWork()
    {
        var query =
            from a in Ok(2)
            from b in Ok(3)
            select a * b;

        query.IsSuccess.ShouldBeTrue();
        query.Value.ShouldBe(6);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Pipeline_ShouldHandleMixedOperations()
    {
        var result = await Ok(1)
            .ThenAsync(
                static (v, _) => ValueTask.FromResult(Ok(v + 1)),
                TestContext.Current.CancellationToken
            )
            .MapAsync(static v => v * 5, TestContext.Current.CancellationToken)
            .TapAsync(
                static (value, _) =>
                {
                    value.ShouldBe(10);
                    return ValueTask.CompletedTask;
                },
                TestContext.Current.CancellationToken
            )
            .EnsureAsync(
                static (value, _) => ValueTask.FromResult(value == 10),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .RecoverAsync(
                static _ => Ok(42),
                TestContext.Current.CancellationToken
            );

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(10);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Cancellation_ShouldPropagateThroughChain()
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapAsync_ShouldReturnCancellationError_WhenTaskCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = ValueTask.FromCanceled<Result<int>>(cts.Token);
        var result = await task.MapAsync(static v => v, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RecoverAsync_ShouldSkip_WhenCancellationOccurs()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = ValueTask.FromCanceled<Result<int>>(cts.Token);
        var result = await task.RecoverAsync(static _ => Ok(5), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public void OnSuccess_ShouldInvokeAction()
    {
        var invoked = false;
        _ = Ok(1).OnSuccess(_ => invoked = true);

        invoked.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask OnSuccessAsync_Result_ShouldInvokeAction()
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

        result.IsSuccess.ShouldBeTrue();
        invoked.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask OnSuccessAsync_TaskResult_WithAsyncAction_ShouldInvoke()
    {
        var invoked = false;
        var result = await ValueTask.FromResult(Ok(3)).OnSuccessAsync(
            async (value, ct) =>
            {
                await Task.Delay(5, ct);
                invoked = value == 3;
            },
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.ShouldBeTrue();
        invoked.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask OnSuccessAsync_ShouldSupportValueTaskAction()
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

        result.IsSuccess.ShouldBeTrue();
        invoked.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask OnFailureAsync_ShouldSupportValueTaskAction()
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

        result.IsFailure.ShouldBeTrue();
        observed.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public void OnFailure_ShouldInvokeAction()
    {
        Error? captured = null;
        _ = Err<int>("fail").OnFailure(err => captured = err);

        captured.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask OnFailureAsync_Result_ShouldInvokeAction()
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

        result.IsFailure.ShouldBeTrue();
        captured.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask OnFailureAsync_TaskResult_WithAsyncAction_ShouldInvoke()
    {
        Error? captured = null;
        var result = await ValueTask.FromResult(Err<int>("fail")).OnFailureAsync(
            async (error, ct) =>
            {
                await Task.Delay(5, ct);
                captured = error;
            },
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        captured.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapErrorAsync_TaskResult_WithAction_ShouldInvoke()
    {
        Error? captured = null;
        var result = await ValueTask.FromResult(Err<int>("fail")).TapErrorAsync(
            error => captured = error,
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        captured.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public void Tee_ShouldBehaveLikeTap()
    {
        var tapped = false;
        var result = Ok(3).Tee(_ => tapped = true);

        result.IsSuccess.ShouldBeTrue();
        tapped.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TeeAsync_TaskResult_WithAction_ShouldInvoke()
    {
        var tapped = false;
        var resultTask = ValueTask.FromResult(Ok("value"));
        var result = await resultTask.TeeAsync(_ => tapped = true, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        tapped.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TeeAsync_TaskResult_WithAsyncSideEffect_ShouldInvoke()
    {
        var tapped = false;
        var resultTask = ValueTask.FromResult(Ok(5));
        var result = await resultTask.TeeAsync(async (_, ct) =>
        {
            await Task.Delay(5, ct);
            tapped = true;
        }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        tapped.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapAsync_TaskResult_ShouldTransformValue()
    {
        var resultTask = ValueTask.FromResult(Ok(2));
        var mapped = await resultTask.MapAsync(static v => v * 3, TestContext.Current.CancellationToken);

        mapped.IsSuccess.ShouldBeTrue();
        mapped.Value.ShouldBe(6);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapAsync_TaskResult_WithAsyncMapper_ShouldTransformValue()
    {
        var resultTask = ValueTask.FromResult(Ok(3));
        var mapped = await resultTask.MapAsync(
            static async (value, ct) =>
            {
                await Task.Delay(5, ct);
                return value * 2;
            },
            TestContext.Current.CancellationToken
        );

        mapped.IsSuccess.ShouldBeTrue();
        mapped.Value.ShouldBe(6);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RecoverAsync_TaskResultWithAsyncRecover_ShouldReturnRecoveredResult()
    {
        var task = ValueTask.FromResult(Err<int>("fail"));
        var recovered = await task.RecoverAsync(static async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return Ok(9);
        }, TestContext.Current.CancellationToken);

        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value.ShouldBe(9);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask EnsureAsync_TaskResult_ShouldFailWhenPredicateFalse()
    {
        var task = ValueTask.FromResult(Ok(1));
        var ensured = await task.EnsureAsync(static async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return false;
        }, cancellationToken: TestContext.Current.CancellationToken);

        ensured.IsFailure.ShouldBeTrue();
        ensured.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FinallyAsync_Result_ShouldInvokeAsyncHandlers()
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

        result.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FinallyAsync_TaskResult_ShouldInvokeAsyncHandlers()
    {
        var outcome = await ValueTask.FromResult(Ok("value")).FinallyAsync(
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

        outcome.ShouldBe("VALUE");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FinallyAsync_TaskResult_ShouldReturnCanceledError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = ValueTask.FromCanceled<Result<int>>(cts.Token);
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

        result.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FinallyAsync_ValueTaskResult_ShouldSupportValueTaskContinuations()
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

        outcome.ShouldBe("value-ok");
    }

    [Fact(Timeout = 15_000)]
    public void Where_ShouldFailWhenPredicateFalse()
    {
        var result = Ok(1).Where(static v => v > 5);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    private static (long FailureCount, Result<T> Result) CaptureFailureDiagnostics<T>(Func<Result<T>> pipeline)
    {
        GoDiagnostics.Reset();

        using var listener = new MeterListener();
        using var meter = new Meter(GoDiagnostics.MeterName);
        long failures = 0;

        listener.InstrumentPublished += (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GoDiagnostics.MeterName && instrument.Name == "result.failures")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Meter.Name == GoDiagnostics.MeterName && instrument.Name == "result.failures")
            {
                failures += measurement;
            }
        });

        listener.Start();
        GoDiagnostics.Configure(meter);

        var result = pipeline();

        listener.RecordObservableInstruments();
        listener.Dispose();
        GoDiagnostics.Reset();

        return (failures, result);
    }
}
