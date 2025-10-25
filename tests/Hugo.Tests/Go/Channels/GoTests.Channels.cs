using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public partial class GoTests
{
    [Fact]
    public async Task SelectFanInAsync_ShouldDrainAllCases()
    {
        var channel1 = MakeChannel<int>();
        var channel2 = MakeChannel<int>();
        var observed = new ConcurrentBag<int>();

        var fanInTask = SelectFanInAsync(
            [channel1.Reader, channel2.Reader],
            (int value, CancellationToken _) =>
            {
                observed.Add(value);
                return Task.FromResult(Result.Ok(Unit.Value));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var producers = Task.WhenAll(
            Task.Run(async () =>
            {
                await channel1.Writer.WriteAsync(11, ct);
                channel1.Writer.TryComplete();
            }, ct),
            Task.Run(async () =>
            {
                await channel2.Writer.WriteAsync(22, ct);
                channel2.Writer.TryComplete();
            }, ct));

        await producers;
        var result = await fanInTask;

        Assert.True(result.IsSuccess);
        Assert.Contains(11, observed);
        Assert.Contains(22, observed);
        Assert.Equal(2, observed.Count);
    }

    [Fact]
    public async Task SelectFanInAsync_ShouldPropagateCaseFailure()
    {
        var channel = MakeChannel<int>();
        var fanInTask = SelectFanInAsync(
            [channel.Reader],
            (int _, CancellationToken _) => Task.FromResult(Result.Fail<Unit>(Error.From("boom", ErrorCodes.Validation))),
            cancellationToken: TestContext.Current.CancellationToken);

        await channel.Writer.WriteAsync(5, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await fanInTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Equal("boom", result.Error?.Message);
    }

    [Fact]
    public async Task SelectFanInAsync_ShouldRespectCancellation()
    {
        var channel = MakeChannel<int>();
        using var cts = new CancellationTokenSource(25);

        var result = await SelectFanInAsync(
            [channel.Reader],
            (int _, CancellationToken _) => Task.FromResult(Result.Ok(Unit.Value)),
            cancellationToken: cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        channel.Writer.TryComplete();
    }

    [Fact]
    public async Task SelectFanInAsync_ShouldTimeoutAfterOverallDeadline()
    {
        var provider = new FakeTimeProvider();
        var channel1 = MakeChannel<int>();
        var channel2 = MakeChannel<int>();

        var selectTask = SelectFanInAsync(
            [channel1.Reader, channel2.Reader],
            (int _, CancellationToken _) => Task.FromResult(Result.Ok(Unit.Value)),
            timeout: TimeSpan.FromSeconds(1),
            provider: provider,
            cancellationToken: TestContext.Current.CancellationToken);

        using var advanceCts = new CancellationTokenSource();
        var advanceLoop = Task.Run(async () =>
        {
            while (!selectTask.IsCompleted && !advanceCts.IsCancellationRequested)
            {
                provider.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Yield();
            }
        }, CancellationToken.None);

        Result<Unit> result;
        try
        {
            result = await selectTask;
        }
        finally
        {
            advanceCts.Cancel();
        await advanceLoop;
        }

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Theory]
    [InlineData(new[] { 1, 2 })]
    public async Task FanInAsync_ShouldMergeIntoDestination(int[] expected)
    {
        var source1 = MakeChannel<int>();
        var source2 = MakeChannel<int>();
        var destination = MakeChannel<int>();

        var ct = TestContext.Current.CancellationToken;
        var fanInTask = FanInAsync([source1.Reader, source2.Reader], destination.Writer, cancellationToken: ct);

        var producers = Task.WhenAll(
            Task.Run(async () =>
            {
                await source1.Writer.WriteAsync(expected[0], ct);
                source1.Writer.TryComplete();
            }, ct),
            Task.Run(async () =>
            {
                await source2.Writer.WriteAsync(expected[^1], ct);
                source2.Writer.TryComplete();
            }, ct));

        await producers;
        var result = await fanInTask;

        Assert.True(result.IsSuccess);

        var observed = new List<int>();
        await foreach (var value in destination.Reader.ReadAllAsync(ct))
        {
            observed.Add(value);
        }

        observed.Sort();
        Assert.Equal(expected, observed);
    }

    [Fact]
    public async Task FanInAsync_ShouldReturnFailure_WhenDestinationClosed()
    {
        var source = MakeChannel<int>();
        var destination = MakeChannel<int>();
        destination.Writer.TryComplete(new InvalidOperationException("closed"));

        var fanInTask = FanInAsync([source.Reader], destination.Writer, cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(42, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var result = await fanInTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await destination.Reader.Completion);
    }

    [Theory]
    [InlineData(new[] { 7, 9 })]
    public async Task FanIn_ShouldMergeSources(int[] expected)
    {
        var source1 = MakeChannel<int>();
        var source2 = MakeChannel<int>();

        var ct = TestContext.Current.CancellationToken;
        var merged = FanIn([source1.Reader, source2.Reader], cancellationToken: ct);

        var producers = Task.WhenAll(
            Task.Run(async () =>
            {
                await source1.Writer.WriteAsync(expected[0], ct);
                source1.Writer.TryComplete();
            }, ct),
            Task.Run(async () =>
            {
                await source2.Writer.WriteAsync(expected[^1], ct);
                source2.Writer.TryComplete();
            }, ct));

        await producers;

        var values = new List<int>();
        await foreach (var value in merged.ReadAllAsync(ct))
        {
            values.Add(value);
        }

        values.Sort();
        Assert.Equal(expected, values);
    }

    [Fact]
    public async Task FanIn_ShouldPropagateCancellation()
    {
        var source = MakeChannel<int>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var merged = FanIn([source.Reader], cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await merged.ReadAsync(cts.Token));
        source.Writer.TryComplete();
    }

    [Fact]
    public async Task FanOutAsync_ShouldBroadcastToAllDestinations()
    {
        var source = MakeChannel<int>();
        var destination1 = MakeChannel<int>();
        var destination2 = MakeChannel<int>();

        var fanOutTask = FanOutAsync(
            source.Reader,
            [destination1.Writer, destination2.Writer],
            cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(17, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var result = await fanOutTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(17, await destination1.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(17, await destination2.Reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FanOutAsync_ShouldTimeoutWhenDeadlineExpires()
    {
        var provider = new FakeTimeProvider();
        var source = MakeChannel<int>();

        var destinationOptions = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        var destination = Channel.CreateBounded<int>(destinationOptions);
        Assert.True(destination.Writer.TryWrite(7));

        var fanOutTask = FanOutAsync(
            source.Reader,
            [destination.Writer],
            deadline: TimeSpan.FromSeconds(1),
            provider: provider,
            cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(13, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        provider.Advance(TimeSpan.FromMilliseconds(1100));

        var step = TimeSpan.FromMilliseconds(200);
        using var advanceCts = new CancellationTokenSource();
        var advanceLoop = Task.Run(async () =>
        {
            while (!fanOutTask.IsCompleted && !advanceCts.IsCancellationRequested)
            {
                provider.Advance(step);
                await Task.Yield();
            }
        }, CancellationToken.None);

        Result<Unit> result;
        try
        {
            result = await fanOutTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await advanceCts.CancelAsync();
            await advanceLoop;
        }

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact]
    public async Task FanOutAsync_ShouldRespectCancellation()
    {
        var source = MakeChannel<int>();
        var destination = MakeChannel<int>();
        using var cts = new CancellationTokenSource();

        var fanOutTask = FanOutAsync(
            source.Reader,
            [destination.Writer],
            cancellationToken: cts.Token);

        await cts.CancelAsync();

        var result = await fanOutTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact]
    public async Task FanOut_ShouldReturnBranchReaders()
    {
        var source = MakeChannel<int>();

        var branches = FanOut(source.Reader, 2, cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(99, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var value1 = await branches[0].ReadAsync(TestContext.Current.CancellationToken);
        var value2 = await branches[1].ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(99, value1);
        Assert.Equal(99, value2);
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await branches[0].ReadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await branches[1].ReadAsync(TestContext.Current.CancellationToken));
    }
}
