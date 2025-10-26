using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;

using Hugo;

using Microsoft.Extensions.Time.Testing;

using static Hugo.GoChannelHelpers;

namespace Hugo.Tests;

public class GoChannelHelpersTests
{
    [Fact]
    public void CollectSources_WithArray_ReturnsSameInstance()
    {
        ChannelReader<int>[] readers = [Channel.CreateUnbounded<int>().Reader];

        ChannelReader<int>[] result = CollectSources(readers);

        Assert.Same(readers, result);
    }

    [Fact]
    public void CollectSources_WithList_ReturnsCopy()
    {
        ChannelReader<int> reader = Channel.CreateUnbounded<int>().Reader;
        List<ChannelReader<int>> list = [reader];

        ChannelReader<int>[] result = CollectSources(list);

        Assert.Single(result, reader);
        Assert.NotSame(list, result);
    }

    [Fact]
    public void CollectSources_WithEnumerable_ReturnsCollected()
    {
        IEnumerable<ChannelReader<int>> CreateReaders()
        {
            yield return Channel.CreateUnbounded<int>().Reader;
            yield return Channel.CreateUnbounded<int>().Reader;
        }

        ChannelReader<int>[] result = CollectSources(CreateReaders());

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void CollectSources_WithEmptyEnumerable_ReturnsEmpty()
    {
        ChannelReader<int>[] result = CollectSources(Array.Empty<ChannelReader<int>>());

        Assert.Empty(result);
    }

    [Fact]
    public async Task SelectFanInAsyncCore_ShouldReturnSuccess_WhenValuesProcessed()
    {
        Channel<int> channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(7, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        Result<Go.Unit> result = await SelectFanInAsyncCore(
            [channel.Reader],
            static (value, _) => Task.FromResult(Result.Ok(Go.Unit.Value)),
            Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    [Fact]
    public async Task SelectFanInAsyncCore_ShouldReturnTimeout_WhenNoValues()
    {
        Channel<int> channel = Channel.CreateUnbounded<int>();

        Result<Go.Unit> result = await SelectFanInAsyncCore(
            [channel.Reader],
            static (_, _) => Task.FromResult(Result.Ok(Go.Unit.Value)),
            TimeSpan.Zero,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure, result.Error?.Code);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact]
    public async Task SelectFanInAsyncCore_ShouldReturnFailure_WhenHandlerFails()
    {
        Channel<int> channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        Result<Go.Unit> result = await SelectFanInAsyncCore(
            [channel.Reader],
            static (_, _) => Task.FromResult(Result.Fail<Go.Unit>(Error.From("boom", ErrorCodes.Validation))),
            Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public async Task SelectFanInAsyncCore_ShouldReturnCanceled_WhenTokenCanceled()
    {
        using var cts = new CancellationTokenSource();
        Channel<int> channel = Channel.CreateUnbounded<int>();

        var task = SelectFanInAsyncCore(
            [channel.Reader],
            static (_, _) => Task.FromResult(Result.Ok(Go.Unit.Value)),
            Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: cts.Token);

        cts.Cancel();

        Result<Go.Unit> result = await task;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task FanInAsyncCore_ShouldPropagateWritesAndCompleteDestination()
    {
        Channel<int> source = Channel.CreateUnbounded<int>();
        Channel<int> destination = Channel.CreateUnbounded<int>();

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        Result<Go.Unit> result = await FanInAsyncCore(
            [source.Reader],
            destination.Writer,
            completeDestination: true,
            timeout: Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Code);

        var values = new List<int>();
        await foreach (int item in destination.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            values.Add(item);
        }

        Assert.Equal([1, 2], values);
        Assert.True(destination.Reader.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FanInAsyncCore_ShouldNotCompleteDestination_WhenFlagFalse()
    {
        Channel<int> source = Channel.CreateUnbounded<int>();
        Channel<int> destination = Channel.CreateUnbounded<int>();

        await source.Writer.WriteAsync(5, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        Result<Go.Unit> result = await FanInAsyncCore(
            [source.Reader],
            destination.Writer,
            completeDestination: false,
            timeout: Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(destination.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task FanInAsyncCore_ReturnsFailure_WhenDestinationClosed()
    {
        Channel<int> source = Channel.CreateUnbounded<int>();
        Channel<int> destination = Channel.CreateUnbounded<int>();
        destination.Writer.TryComplete(new InvalidOperationException("closed"));

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        Result<Go.Unit> result = await FanInAsyncCore(
            [source.Reader],
            destination.Writer,
            completeDestination: true,
            timeout: Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
    }

    [Fact]
    public async Task FanOutAsyncCore_ShouldBroadcastToDestinations()
    {
        Channel<int> source = Channel.CreateUnbounded<int>();
        Channel<int> destination1 = Channel.CreateUnbounded<int>();
        Channel<int> destination2 = Channel.CreateUnbounded<int>();

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        Result<Go.Unit> result = await FanOutAsyncCore(
            source.Reader,
            [destination1.Writer, destination2.Writer],
            completeDestinations: true,
            deadline: Timeout.InfiniteTimeSpan,
            provider: TimeProvider.System,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);

        var values1 = new List<int>();
        await foreach (int item in destination1.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            values1.Add(item);
        }

        var values2 = new List<int>();
        await foreach (int item in destination2.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            values2.Add(item);
        }

        Assert.Equal([1, 2], values1);
        Assert.Equal([1, 2], values2);
        Assert.True(destination1.Reader.Completion.IsCompletedSuccessfully);
        Assert.True(destination2.Reader.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FanOutAsyncCore_ReturnsFailure_WhenDestinationClosed()
    {
        Channel<int> source = Channel.CreateUnbounded<int>();
        Channel<int> destination = Channel.CreateUnbounded<int>();
        destination.Writer.TryComplete();

        await source.Writer.WriteAsync(42, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        Result<Go.Unit> result = await FanOutAsyncCore(
            source.Reader,
            [destination.Writer],
            completeDestinations: true,
            deadline: Timeout.InfiniteTimeSpan,
            provider: TimeProvider.System,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ChannelCompleted, result.Error?.Code);
    }

    [Fact]
    public async Task FanOutAsyncCore_ReturnsTimeout_WhenDeadlineElapsed()
    {
        Channel<int> source = Channel.CreateUnbounded<int>();
        var blockingWriter = new BlockingWriter<int>();

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var provider = new FakeTimeProvider();
        var task = FanOutAsyncCore(
            source.Reader,
            [blockingWriter],
            completeDestinations: true,
            deadline: TimeSpan.FromSeconds(1),
            provider: provider,
            cancellationToken: TestContext.Current.CancellationToken);

        _ = Task.Run(async () =>
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            provider.Advance(TimeSpan.FromSeconds(2));
        }, TestContext.Current.CancellationToken);

        Result<Go.Unit> result = await task;

        Assert.True(result.IsFailure, result.Error?.Code);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
        Assert.True(blockingWriter.Completed);
    }

    [Fact]
    public async Task FanOutAsyncCore_ReturnsCanceled_WhenTokenCancelled()
    {
        Channel<int> source = Channel.CreateUnbounded<int>();
        Channel<int> destination = Channel.CreateUnbounded<int>();
        using CancellationTokenSource cts = new();

        var task = FanOutAsyncCore(
            source.Reader,
            [destination.Writer],
            completeDestinations: true,
            deadline: Timeout.InfiniteTimeSpan,
            provider: TimeProvider.System,
            cancellationToken: cts.Token);

        cts.Cancel();

        Result<Go.Unit> result = await task;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public void CompleteWriters_ShouldCompleteWithAndWithoutException()
    {
        Channel<int> channel1 = Channel.CreateUnbounded<int>();
        Channel<int> channel2 = Channel.CreateUnbounded<int>();

        CompleteWriters([channel1.Writer, channel2.Writer], exception: null);

        Assert.True(channel1.Reader.Completion.IsCompletedSuccessfully);
        Assert.True(channel2.Reader.Completion.IsCompletedSuccessfully);

        Channel<int> channel3 = Channel.CreateUnbounded<int>();
        CompleteWriters([channel3.Writer], new InvalidOperationException());

        Assert.True(channel3.Reader.Completion.IsFaulted);
    }

    [Fact]
    public void CreateChannelOperationException_ShouldConvertCanceledErrorWithToken()
    {
        using CancellationTokenSource cts = new();
        Error error = Error.Canceled(token: cts.Token);

        Exception exception = CreateChannelOperationException(error);

        Assert.IsType<OperationCanceledException>(exception);
    }

    [Fact]
    public void CreateChannelOperationException_ShouldReturnCause()
    {
        var cause = new InvalidOperationException("boom");
        Error error = Error.From("failure").WithCause(cause);

        Exception exception = CreateChannelOperationException(error);

        Assert.Same(cause, exception);
    }
}

internal sealed class BlockingWriter<T> : ChannelWriter<T>
{
    private readonly TaskCompletionSource<bool> _waitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Completed { get; private set; }

    public override bool TryWrite(T item) => false;

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        return new ValueTask<bool>(_waitSource.Task.WaitAsync(cancellationToken));
    }

    public override bool TryComplete(Exception? error = null)
    {
        Completed = true;
        _waitSource.TrySetResult(false);
        return true;
    }
}
