using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Shouldly;

using static Hugo.GoChannelHelpers;

namespace Hugo.Tests;

public class GoChannelHelpersTests
{
    [Fact(Timeout = 15_000)]
    public void CollectSources_WithArray_ReturnsSameInstance()
    {
        ChannelReader<int>[] readers = [Channel.CreateUnbounded<int>().Reader];

        ChannelReader<int>[] result = CollectSources(readers);

        result.ShouldBeSameAs(readers);
    }

    [Fact(Timeout = 15_000)]
    public void CollectSources_WithList_ReturnsCopy()
    {
        ChannelReader<int> reader = Channel.CreateUnbounded<int>().Reader;
        List<ChannelReader<int>> list = [reader];

        ChannelReader<int>[] result = CollectSources(list);

        var item = result.ShouldHaveSingleItem();
        item.ShouldBeSameAs(reader);
        result.ShouldNotBeSameAs(list);
    }

    [Fact(Timeout = 15_000)]
    public void CollectSources_WithEnumerable_ReturnsCollected()
    {
        IEnumerable<ChannelReader<int>> CreateReaders()
        {
            yield return Channel.CreateUnbounded<int>().Reader;
            yield return Channel.CreateUnbounded<int>().Reader;
        }

        ChannelReader<int>[] result = CollectSources(CreateReaders());

        result.Length.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public void CollectSources_WithEmptyEnumerable_ReturnsEmpty()
    {
        ChannelReader<int>[] result = CollectSources(Array.Empty<ChannelReader<int>>());

        result.ShouldBeEmpty();
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsyncCore_ShouldReturnSuccess_WhenValuesProcessed()
    {
        Channel<int> channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(7, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        Result<Go.Unit> result = await SelectFanInAsyncCore(
            [channel.Reader],
            static (value, _) => ValueTask.FromResult(Result.Ok(Go.Unit.Value)),
            Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsyncCore_ShouldReturnTimeout_WhenNoValues()
    {
        Channel<int> channel = Channel.CreateUnbounded<int>();

        Result<Go.Unit> result = await SelectFanInAsyncCore(
            [channel.Reader],
            static (_, _) => ValueTask.FromResult(Result.Ok(Go.Unit.Value)),
            TimeSpan.Zero,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue(result.Error?.Code);
        result.Error?.Code.ShouldBe(ErrorCodes.Timeout);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsyncCore_ShouldReturnFailure_WhenHandlerFails()
    {
        Channel<int> channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        Result<Go.Unit> result = await SelectFanInAsyncCore(
            [channel.Reader],
            static (_, _) => ValueTask.FromResult(Result.Fail<Go.Unit>(Error.From("boom", ErrorCodes.Validation))),
            Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsyncCore_ShouldReturnCanceled_WhenTokenCanceled()
    {
        using var cts = new CancellationTokenSource();
        Channel<int> channel = Channel.CreateUnbounded<int>();

        var task = SelectFanInAsyncCore(
            [channel.Reader],
            static (_, _) => ValueTask.FromResult(Result.Ok(Go.Unit.Value)),
            Timeout.InfiniteTimeSpan,
            provider: null,
            cancellationToken: cts.Token);

        await cts.CancelAsync();

        Result<Go.Unit> result = await task;

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
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

        result.IsSuccess.ShouldBeTrue(result.Error?.Code);

        var values = new List<int>();
        await foreach (int item in destination.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            values.Add(item);
        }

        values.ShouldBe([1, 2]);
        destination.Reader.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
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

        result.IsSuccess.ShouldBeTrue();
        destination.Reader.Completion.IsCompleted.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
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

        result.IsSuccess.ShouldBeTrue();

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

        values1.ShouldBe([1, 2]);
        values2.ShouldBe([1, 2]);
        destination1.Reader.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        destination2.Reader.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.ChannelCompleted);
    }

    [Fact(Timeout = 15_000)]
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

        result.IsFailure.ShouldBeTrue(result.Error?.Code);
        result.Error?.Code.ShouldBe(ErrorCodes.Timeout);
        blockingWriter.Completed.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
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

        await cts.CancelAsync();

        Result<Go.Unit> result = await task;

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public void CompleteWriters_ShouldCompleteWithAndWithoutException()
    {
        Channel<int> channel1 = Channel.CreateUnbounded<int>();
        Channel<int> channel2 = Channel.CreateUnbounded<int>();

        CompleteWriters([channel1.Writer, channel2.Writer], exception: null);

        channel1.Reader.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        channel2.Reader.Completion.IsCompletedSuccessfully.ShouldBeTrue();

        Channel<int> channel3 = Channel.CreateUnbounded<int>();
        CompleteWriters([channel3.Writer], new InvalidOperationException());

        channel3.Reader.Completion.IsFaulted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public void CreateChannelOperationException_ShouldConvertCanceledErrorWithToken()
    {
        using CancellationTokenSource cts = new();
        Error error = Error.Canceled(token: cts.Token);

        Exception exception = CreateChannelOperationException(error);

        exception.ShouldBeOfType<OperationCanceledException>();
    }

    [Fact(Timeout = 15_000)]
    public void CreateChannelOperationException_ShouldReturnCause()
    {
        var cause = new InvalidOperationException("boom");
        Error error = Error.From("failure").WithCause(cause);

        Exception exception = CreateChannelOperationException(error);

        exception.ShouldBeSameAs(cause);
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
