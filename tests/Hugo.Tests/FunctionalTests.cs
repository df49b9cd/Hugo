// Import both the core and functional helpers
using static Hugo.Go;

namespace Hugo.Tests;

public class FunctionalTests
{
    // --- Core Helper and Error Tests ---
    [Fact]
    public void Err_ShouldReturnDefaultError_WhenGivenNull()
    {
        var (_, e) = Err<string>((Error?)null);
        Assert.NotNull(e);
        Assert.Equal("An unspecified error occurred.", e.Message);
    }

    [Fact]
    public void Error_Constructor_ShouldThrow_OnNullMessage() =>
        Assert.Throws<ArgumentNullException>(() => new Error(null!));

    [Fact]
    public void Error_ShouldImplicitlyConvert_FromString()
    {
        Error? e = "msg";
        Assert.NotNull(e);
        Assert.Equal("msg", e.Message);
    }

    [Fact]
    public void Error_ImplicitConversion_ShouldBeNull_ForNullString()
    {
        Error? e = (string?)null;
        Assert.Null(e);
    }

    [Fact]
    public void Error_ShouldImplicitlyConvert_FromException()
    {
        Error? e = new InvalidOperationException("msg");
        Assert.NotNull(e);
        Assert.Equal("msg", e.Message);
    }

    [Fact]
    public void Error_ImplicitConversion_ShouldBeNull_ForNullException()
    {
        Error? e = (Exception?)null;
        Assert.Null(e);
    }

    // --- Then Tests ---
    [Fact]
    public async Task Functional_ThenAsync_ShouldChain_SyncFunc_After_AsyncTask()
    {
        var (v, e) = await Task.FromResult(Ok("s"))
            .ThenAsync(s => Ok($"{s}c"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(e);
        Assert.Equal("sc", v);
    }

    [Fact]
    public async Task Functional_ThenAsync_ShouldChain_AsyncFunc_After_AsyncTask()
    {
        var (v, e) = await Task.FromResult(Ok("s"))
            .ThenAsync(
                (s, _) => Task.FromResult(Ok($"{s}ac")),
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.Null(e);
        Assert.Equal("sac", v);
    }

    [Fact]
    public async Task Functional_ThenAsync_ShouldPassThroughError_FromAsyncTask()
    {
        var i = new Error("e");
        var t = Task.FromResult(Err<string>(i));
        var (_, e) = await t.ThenAsync(
            Ok,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(i, e);
        (_, e) = await t.ThenAsync(
            (s, _) => Task.FromResult(Ok(s)),
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(i, e);
    }

    // --- Map Tests ---
    [Fact]
    public void Functional_Map_ShouldTransformValue_OnSuccess()
    {
        var (v, e) = Ok(5).Map(x => x * 2);
        Assert.Null(e);
        Assert.Equal(10, v);
    }

    [Fact]
    public async Task Functional_MapAsync_ShouldTransformValue_OnSuccess()
    {
        var (v, e) = await Task.FromResult(Ok(5))
            .MapAsync(x => x * 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(e);
        Assert.Equal(10, v);
    }

    [Fact]
    public void Functional_MapShouldNotExecute_OnFailure()
    {
        var x = false;
        var i = Err<int>("e");
        var (_, e) = i.Map(n =>
        {
            x = true;
            return n;
        });
        Assert.False(x);
        Assert.Same(i.Err, e);
    }

    [Fact]
    public async Task Functional_MapAsyncShouldNotExecute_OnFailure()
    {
        var x = false;
        var i = Task.FromResult(Err<int>("e"));
        var (_, e) = await i.MapAsync(
            n =>
            {
                x = true;
                return n;
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(x);
        Assert.Same((await i).Err, e);
    }

    // --- Tee Tests ---
    [Fact]
    public void Functional_Tee_ShouldExecuteAction_OnSuccess()
    {
        var x = false;
        var (_, e) = Ok("s").Tee(_ => x = true);
        Assert.True(x);
        Assert.Null(e);
    }

    [Fact]
    public async Task Functional_TeeAsync_ShouldExecuteAction_OnSuccess()
    {
        var x = false;
        var (_, e) = await Task.FromResult(Ok("s"))
            .TeeAsync(_ => x = true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(x);
        Assert.Null(e);
    }

    [Fact]
    public void Functional_TeeShouldNotExecute_OnFailure()
    {
        var x = false;
        var i = Err<string>("e");
        var (_, e) = i.Tee(_ => x = true);
        Assert.False(x);
        Assert.Same(i.Err, e);
    }

    [Fact]
    public async Task Functional_TeeAsyncShouldNotExecute_OnFailure()
    {
        var x = false;
        var i = Task.FromResult(Err<string>("e"));
        var (_, e) = await i.TeeAsync(
            _ => x = true,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(x);
        Assert.Same((await i).Err, e);
    }

    // --- Chaining Tests ---
    [Fact]
    public void Functional_ShouldCorrectly_ChainMultipleSyncOperations()
    {
        var f = false;
        var r = Ok(5)
            .Then(i => Ok(i + 5))
            .Map(i => i * 2)
            .Tee(_ => f = true)
            .Then(i => Ok(i.ToString()))
            .Finally(s => $"S:{s}", _ => "E");
        Assert.True(f);
        Assert.Equal("S:20", r);
    }

    [Fact]
    public void Functional_ShouldCorrectly_ChainAndRecover_FromSyncFailure()
    {
        var f = false;
        var r = Ok("s")
            .Then(_ => Err<string>("F"))
            .Map(s =>
            {
                f = true;
                return s;
            })
            .Recover(e => e.Message == "F" ? Ok("R") : Err<string>(e))
            .Map(s => $"{s}F")
            .Finally(s => $"S:{s}", _ => "E");
        Assert.False(f);
        Assert.Equal("S:RF", r);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_ChainMultipleAsyncOperations()
    {
        var f = false;
        var r = await Task.FromResult(Ok(10))
            .ThenAsync(
                (i, _) => Task.FromResult(Ok(i + 10)),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(i => i * 2, cancellationToken: TestContext.Current.CancellationToken)
            .TeeAsync(_ => f = true, cancellationToken: TestContext.Current.CancellationToken)
            .ThenAsync(
                (i, _) => Task.FromResult(Ok(i.ToString())),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .FinallyAsync(
                s => $"S:{s}",
                _ => "E",
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.True(f);
        Assert.Equal("S:40", r);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_ChainAndRecover_FromAsyncFailure()
    {
        var f = false;
        var r = await Task.FromResult(Ok("s"))
            .ThenAsync(
                (_, _) => Task.FromResult(Err<string>("AF")),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(
                s =>
                {
                    f = true;
                    return s;
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .RecoverAsync(
                e => e.Message == "AF" ? Ok("AR") : Err<string>(e),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(s => $"{s}F", cancellationToken: TestContext.Current.CancellationToken)
            .FinallyAsync(
                s => $"S:{s}",
                _ => "E",
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.False(f);
        Assert.Equal("S:ARF", r);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_ChainMixedSyncAndAsyncOperations()
    {
        var f = false;
        var r = await Ok(5)
            .ThenAsync(
                (i, _) => Task.FromResult(Ok(i + 5)),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .MapAsync(i => i * 2, cancellationToken: TestContext.Current.CancellationToken)
            .TeeAsync(_ => f = true, cancellationToken: TestContext.Current.CancellationToken)
            .ThenAsync(
                i => Ok(i.ToString()),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .FinallyAsync(
                s => $"S:{s}",
                _ => "E",
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.True(f);
        Assert.Equal("S:20", r);
    }

    // --- Cancellation, Recovery, and Finally Tests ---
    [Fact]
    public async Task Functional_ShouldReturnCancellationError_WhenThenAsyncIsCancelled()
    {
        var c = new CancellationTokenSource();
        var t = Ok("s")
            .ThenAsync(
                async (_, ct) =>
                {
                    await Task.Delay(5000, ct);
                    return Ok("f");
                },
                c.Token
            );
        c.CancelAfter(100);
        var (_, e) = await t;
        Assert.Same(CancellationError, e);
    }

    [Fact]
    public async Task Functional_MapAsync_ShouldReturnCancellationError_WhenTaskIsCancelled()
    {
        var c = new CancellationTokenSource();
        await c.CancelAsync();
        var t = Task.FromCanceled<(string, Error?)>(c.Token);
        var (_, e) = await t.MapAsync(
            s => s,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(CancellationError, e);
    }

    [Fact]
    public async Task Functional_TeeAsync_ShouldReturnCancellationError_WhenTaskIsCancelled()
    {
        var c = new CancellationTokenSource();
        await c.CancelAsync();
        var t = Task.FromCanceled<(string, Error?)>(c.Token);
        var (_, e) = await t.TeeAsync(
            _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(CancellationError, e);
    }

    [Fact]
    public async Task Functional_RecoverAsync_ShouldReturnCancellationError_WhenTaskIsCancelled()
    {
        var c = new CancellationTokenSource();
        await c.CancelAsync();
        var t = Task.FromCanceled<(string, Error?)>(c.Token);
        var (_, e) = await t.RecoverAsync(
            _ => Ok("r"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Same(CancellationError, e);
    }

    [Fact]
    public async Task Functional_FinallyAsync_ShouldHandleCancellation_WhenTaskIsCancelled()
    {
        var c = new CancellationTokenSource();
        await c.CancelAsync();
        var t = Task.FromCanceled<(string, Error?)>(c.Token);
        var r = await t.FinallyAsync(
            _ => "S",
            e => e.Message,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(CancellationError.Message, r);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_Cancel_ChainedAsyncOperations()
    {
        var c = new CancellationTokenSource();
        var step2Flag = false;
        var t = Ok("start")
            .ThenAsync(
                async (s, ct) =>
                {
                    await Task.Delay(5000, ct);
                    return Ok($"{s}-step1");
                },
                c.Token
            )
            .ThenAsync(
                (s, _) =>
                {
                    step2Flag = true;
                    return Task.FromResult(Ok($"{s}-step2"));
                },
                c.Token
            );
        c.CancelAfter(100);
        var (_, e) = await t;
        Assert.False(step2Flag);
        Assert.Same(CancellationError, e);
    }

    [Fact]
    public async Task Functional_ShouldCorrectly_Cancel_AndBypassRecovery()
    {
        var c = new CancellationTokenSource();
        var recoverFlag = false;
        var t = Ok("start")
            .ThenAsync(
                async (s, ct) =>
                {
                    await Task.Delay(5000, ct);
                    return Ok($"{s}-step1");
                },
                c.Token
            )
            .RecoverAsync(
                _ =>
                {
                    recoverFlag = true;
                    return Ok("recovered");
                },
                c.Token
            );
        c.CancelAfter(100);
        var (_, e) = await t;
        Assert.False(recoverFlag);
        Assert.Same(CancellationError, e);
    }
}
