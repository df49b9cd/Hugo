// Import both the core and functional helpers
using static Hugo.Go;

namespace Hugo.Tests;

public class GoFunctionalTests
{
    // --- Core Helper and Error Tests ---

    [Fact]
    public void Err_ShouldReturnDefaultError_WhenGivenNull()
    {
        var (_, err) = Err<string>((Error?)null);
        Assert.NotNull(err);
        Assert.Equal("An unspecified error occurred.", err.Message);
    }

    [Fact]
    public void Error_Constructor_ShouldThrow_OnNullMessage()
    {
        Assert.Throws<ArgumentNullException>(() => new Error(null!));
    }

    [Fact]
    public void Error_ShouldImplicitlyConvert_FromString()
    {
        const string errorMessage = "This is a test error.";
        Error? err = errorMessage;
        Assert.NotNull(err);
        Assert.Equal(errorMessage, err.Message);
    }

    [Fact]
    public void Error_ImplicitConversion_ShouldBeNull_ForNullString()
    {
        Error? err = (string?)null;
        Assert.Null(err);
    }

    [Fact]
    public void Error_ShouldImplicitlyConvert_FromException()
    {
        var exception = new InvalidOperationException("This is an exception.");
        Error? err = exception;
        Assert.NotNull(err);
        Assert.Equal(exception.Message, err.Message);
    }

    [Fact]
    public void Error_ImplicitConversion_ShouldBeNull_ForNullException()
    {
        Error? err = (Exception?)null;
        Assert.Null(err);
    }

    // --- Then Tests ---

    [Fact]
    public async Task Functional_ThenAsync_ShouldChain_SyncFunc_After_AsyncTask()
    {
        var initialTask = Task.FromResult(Ok("start"));
        (string, Error?) SyncStep(string input) => Ok($"{input}-chained");
        var (value, err) = await initialTask.ThenAsync(
            SyncStep,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Null(err);
        Assert.Equal("start-chained", value);
    }

    [Fact]
    public async Task Functional_ThenAsync_ShouldChain_AsyncFunc_After_AsyncTask()
    {
        var initialTask = Task.FromResult(Ok("start"));

        Task<(string, Error?)> AsyncStep(string input, CancellationToken _) =>
            Task.FromResult(Ok($"{input}-async-chained"));

        var (value, err) = await initialTask.ThenAsync(
            AsyncStep,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Null(err);
        Assert.Equal("start-async-chained", value);
    }

    [Fact]
    public async Task Functional_ThenAsync_ShouldPassThroughError_FromAsyncTask()
    {
        var initialError = new Error("Initial error");
        var initialTask = Task.FromResult(Err<string>(initialError));
        var (_, err) = await initialTask.ThenAsync(
            Ok,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(initialError, err);
        (_, err) = await initialTask.ThenAsync(
            (s, _) => Task.FromResult(Ok(s)),
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(initialError, err);
    }

    // --- Map Tests ---

    [Fact]
    public void Functional_Map_ShouldTransformValue_OnSuccess()
    {
        var (value, err) = Ok(5).Map(x => x * 2);
        Assert.Null(err);
        Assert.Equal(10, value);
    }

    [Fact]
    public async Task Functional_MapAsync_ShouldTransformValue_OnSuccess()
    {
        var (value, err) = await Task.FromResult(Ok(5))
            .MapAsync(x => x * 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(err);
        Assert.Equal(10, value);
    }

    [Fact]
    public void Functional_MapShouldNotExecute_OnFailure()
    {
        var mapFunctionExecuted = false;
        var initialError = Err<int>("Initial error");
        var (_, err) = initialError.Map(i =>
        {
            mapFunctionExecuted = true;
            return i;
        });
        Assert.False(mapFunctionExecuted);
        Assert.Same(initialError.Err, err);
    }

    [Fact]
    public async Task Functional_MapAsyncShouldNotExecute_OnFailure()
    {
        var mapFunctionExecuted = false;
        var initialErrorTask = Task.FromResult(Err<int>("Initial async error"));
        var (_, err) = await initialErrorTask.MapAsync(
            i =>
            {
                mapFunctionExecuted = true;
                return i;
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(mapFunctionExecuted);
        var initialError = await initialErrorTask;
        Assert.Same(initialError.Err, err);
    }

    // --- Tee Tests ---

    [Fact]
    public void Functional_Tee_ShouldExecuteAction_OnSuccess()
    {
        var teeActionExecuted = false;
        var (value, err) = Ok("Success").Tee(_ => teeActionExecuted = true);
        Assert.True(teeActionExecuted);
        Assert.Null(err);
        Assert.Equal("Success", value);
    }

    [Fact]
    public async Task Functional_TeeAsync_ShouldExecuteAction_OnSuccess()
    {
        var teeActionExecuted = false;
        var (value, err) = await Task.FromResult(Ok("Success"))
            .TeeAsync(
                _ => teeActionExecuted = true,
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.True(teeActionExecuted);
        Assert.Null(err);
        Assert.Equal("Success", value);
    }

    [Fact]
    public void Functional_TeeShouldNotExecute_OnFailure()
    {
        var teeActionExecuted = false;
        var initialError = Err<string>("Initial error");
        var (_, err) = initialError.Tee(_ => teeActionExecuted = true);
        Assert.False(teeActionExecuted);
        Assert.Same(initialError.Err, err);
    }

    [Fact]
    public async Task Functional_TeeAsyncShouldNotExecute_OnFailure()
    {
        var teeActionExecuted = false;
        var initialErrorTask = Task.FromResult(Err<string>("Initial async error"));
        var (_, err) = await initialErrorTask.TeeAsync(
            _ => teeActionExecuted = true,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(teeActionExecuted);
        var initialError = await initialErrorTask;
        Assert.Same(initialError.Err, err);
    }

    // --- Chaining Tests ---

    [Fact]
    public void Functional_ShouldCorrectly_ChainMultipleSyncOperations()
    {
        var teeFlag = false;
        var result = Ok(5)
            .Then(i => Ok(i + 5))
            .Map(i => i * 2)
            .Tee(_ => teeFlag = true)
            .Then(i => Ok(i.ToString()))
            .Finally(s => $"Success: {s}", _ => "Error");
        Assert.True(teeFlag);
        Assert.Equal("Success: 20", result);
    }

    [Fact]
    public void Functional_ShouldCorrectly_ChainAndRecover_FromSyncFailure()
    {
        var mapFlag = false;
        var result = Ok("start")
            .Then(_ => Err<string>("Failure point"))
            .Map(s =>
            {
                mapFlag = true;
                return s.ToUpper();
            })
            .Recover(e => e.Message.Contains("Failure point") ? Ok("Recovered") : Err<string>(e))
            .Map(s => $"{s}-Final")
            .Finally(s => $"Success: {s}", _ => "Error");
        Assert.False(mapFlag);
        Assert.Equal("Success: Recovered-Final", result);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_ChainMultipleAsyncOperations()
    {
        var teeFlag = false;
        var result = await Task.FromResult(Ok(10))
            .ThenAsync(
                (i, _) => Task.FromResult(Ok(i + 10)),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(i => i * 2, cancellationToken: TestContext.Current.CancellationToken)
            .TeeAsync(_ => teeFlag = true, cancellationToken: TestContext.Current.CancellationToken)
            .ThenAsync(
                (i, _) => Task.FromResult(Ok(i.ToString())),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .FinallyAsync(
                s => $"Success: {s}",
                _ => "Error",
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.True(teeFlag);
        Assert.Equal("Success: 40", result);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_ChainAndRecover_FromAsyncFailure()
    {
        var mapFlag = false;
        var result = await Task.FromResult(Ok("start"))
            .ThenAsync(
                (_, _) => Task.FromResult(Err<string>("Async failure")),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(
                s =>
                {
                    mapFlag = true;
                    return s.ToUpper();
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .RecoverAsync(
                e => e.Message.Contains("Async failure") ? Ok("Async Recovered") : Err<string>(e),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(s => $"{s}-Final", cancellationToken: TestContext.Current.CancellationToken)
            .FinallyAsync(
                s => $"Success: {s}",
                _ => "Error",
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.False(mapFlag);
        Assert.Equal("Success: Async Recovered-Final", result);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_ChainMixedSyncAndAsyncOperations()
    {
        var teeFlag = false;
        var result = await Ok(5)
            .ThenAsync(
                (i, _) => Task.FromResult(Ok(i + 5)),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(i => i * 2, cancellationToken: TestContext.Current.CancellationToken)
            .TeeAsync(_ => teeFlag = true, cancellationToken: TestContext.Current.CancellationToken)
            .ThenAsync(
                i => Ok(i.ToString()),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .FinallyAsync(
                s => $"Success: {s}",
                _ => "Error",
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.True(teeFlag);
        Assert.Equal("Success: 20", result);
    }

    // --- Cancellation, Recovery, and Finally Tests ---

    [Fact]
    public async Task Functional_ShouldReturnCancellationError_WhenThenAsyncIsCancelled()
    {
        var cts = new CancellationTokenSource();

        async Task<(string, Error?)> LongRunningStep(string _, CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            return Ok("fail");
        }

        var pipelineTask = Ok("start").ThenAsync(LongRunningStep, cts.Token);
        cts.CancelAfter(100);
        var (_, err) = await pipelineTask;
        Assert.Same(CancellationError, err);
    }

    [Fact]
    public async Task Functional_MapAsync_ShouldReturnCancellationError_WhenTaskIsCancelled()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var canceledTask = Task.FromCanceled<(string, Error?)>(cts.Token);
        var (_, err) = await canceledTask.MapAsync(
            s => s,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(CancellationError, err);
    }

    [Fact]
    public async Task Functional_TeeAsync_ShouldReturnCancellationError_WhenTaskIsCancelled()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var canceledTask = Task.FromCanceled<(string, Error?)>(cts.Token);
        var (_, err) = await canceledTask.TeeAsync(
            _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(CancellationError, err);
    }

    [Fact]
    public async Task Functional_RecoverAsync_ShouldReturnCancellationError_WhenTaskIsCancelled()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var canceledTask = Task.FromCanceled<(string, Error?)>(cts.Token);
        var (_, err) = await canceledTask.RecoverAsync(
            _ => Ok("recovered"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(CancellationError, err);
    }

    [Fact]
    public async Task Functional_FinallyAsync_ShouldHandleCancellation_WhenTaskIsCancelled()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var canceledTask = Task.FromCanceled<(string, Error?)>(cts.Token);
        var result = await canceledTask.FinallyAsync(
            onSuccess: _ => "Success",
            onError: err => err.Message,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(CancellationError.Message, result);
    }

    [Fact]
    public async Task Functional_ShouldCancel_InTheMiddleOfAChain()
    {
        var cts = new CancellationTokenSource();
        var finalStepExecuted = false;

        async Task<(string, Error?)> LongRunningStep(string input, CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            return Ok($"{input}-long");
        }

        var pipelineTask = Ok("start")
            .Map(s => $"{s}-step1") // Sync step
            .ThenAsync(LongRunningStep, cts.Token) // Async step that will be canceled
            .TeeAsync(
                _ => finalStepExecuted = true,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .FinallyAsync(
                s => "Success",
                e => e.Message,
                cancellationToken: TestContext.Current.CancellationToken
            );

        cts.CancelAfter(100);

        var result = await pipelineTask;

        Assert.False(
            finalStepExecuted,
            "Pipeline should have been cancelled before the final step."
        );
        Assert.Equal(CancellationError.Message, result);
    }
}
