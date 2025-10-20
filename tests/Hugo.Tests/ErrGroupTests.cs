using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ErrGroupTests
{
    [Fact]
    public async Task WaitAsync_ShouldReturnSuccess_WhenAllOperationsComplete()
    {
        using var group = new ErrGroup();
        var counter = 0;

        group.Go(async ct =>
        {
            await Task.Yield();
            Interlocked.Increment(ref counter);
            return Result.Ok(Unit.Value);
        });

        group.Go(() =>
        {
            Interlocked.Increment(ref counter);
            return Task.CompletedTask;
        });

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, counter);
        Assert.Null(group.Error);
        Assert.False(group.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task WaitAsync_ShouldReturnFirstError_AndCancelRemainingOperations()
    {
        using var group = new ErrGroup();
        var enteredSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        group.Go(async ct =>
        {
            await enteredSecond.Task.ConfigureAwait(false);
            return Result.Fail<Unit>(Error.From("boom", ErrorCodes.Exception));
        });

        group.Go(async ct =>
        {
            enteredSecond.TrySetResult(true);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                cancellationObserved.TrySetResult(false);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
            }

            return Result.Ok(Unit.Value);
        });

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsFailure);
    Assert.Equal("boom", result.Error?.Message);
    Assert.Equal(group.Error?.Message, result.Error?.Message);
    Assert.True(group.Token.IsCancellationRequested);
    Assert.True(result.Error?.Code == ErrorCodes.Exception);
    Assert.True(await cancellationObserved.Task);
    }

    [Fact]
    public async Task WaitAsync_ShouldSurfaceExceptionsAsStructuredErrors()
    {
        using var group = new ErrGroup();

        group.Go(async ct =>
        {
            await Task.Yield();
            throw new InvalidOperationException("explode");
        });

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Equal("explode", result.Error?.Message);
        Assert.True(group.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task WaitAsync_ShouldReturnCanceled_WhenLinkedTokenCancels()
    {
        using var externalCts = new CancellationTokenSource();
        using var group = new ErrGroup(externalCts.Token);

        group.Go(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Result.Ok(Unit.Value);
        });

        externalCts.Cancel();

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.True(group.Token.IsCancellationRequested);
    }
}
