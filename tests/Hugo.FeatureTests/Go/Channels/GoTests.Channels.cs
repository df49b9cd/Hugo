using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public partial class GoTests
{
    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithCapacity_UsesBoundedConfiguration()
    {
        var channel = MakeChannel<int>(
            capacity: 2,
            fullMode: BoundedChannelFullMode.DropOldest,
            singleReader: true,
            singleWriter: true);

        Assert.True(channel.Writer.TryWrite(1));
        Assert.True(channel.Writer.TryWrite(2));
        Assert.True(channel.Writer.TryWrite(3));

        Assert.True(channel.Reader.TryRead(out var first));
        Assert.Equal(2, first);
        Assert.True(channel.Reader.TryRead(out var second));
        Assert.Equal(3, second);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_ShouldApplyBoundedBuilderDelegate()
    {
        BoundedChannelOptions? capturedOptions = null;

        Channel<int> channel = MakeChannel<int>(
            capacity: 4,
            configureBounded: builder => builder
                .AllowSynchronousContinuations()
                .Configure(options => capturedOptions = options));

        Assert.NotNull(channel);
        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions!.AllowSynchronousContinuations);
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_ShouldApplyUnboundedBuilderDelegate()
    {
        UnboundedChannelOptions? capturedOptions = null;

        Channel<int> channel = MakeChannel<int>(
            configureUnbounded: builder => builder
                .AllowSynchronousContinuations()
                .Configure(options => capturedOptions = options));

        Assert.NotNull(channel);
        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions!.AllowSynchronousContinuations);
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithZeroCapacity_UsesUnboundedConfiguration()
    {
        var channel = MakeChannel<int>(capacity: 0, singleReader: true, singleWriter: true);

        for (var i = 0; i < 64; i++)
        {
            Assert.True(channel.Writer.TryWrite(i));
        }

        for (var i = 0; i < 64; i++)
        {
            Assert.True(channel.Reader.TryRead(out var value));
            Assert.Equal(i, value);
        }
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithBoundedOptions_AppliesSettings()
    {
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        };

        var channel = MakeChannel<int>(options);

        Assert.True(channel.Writer.TryWrite(10));
        Assert.True(channel.Writer.TryWrite(20));
        Assert.True(channel.Reader.TryRead(out var value));
        Assert.Equal(20, value);
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithBoundedOptionsNull_Throws() =>
        Assert.Throws<ArgumentNullException>(static () => MakeChannel<int>((BoundedChannelOptions)null!));

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithUnboundedOptions_CreatesChannel()
    {
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        };

        var channel = MakeChannel<int>(options);

        for (var i = 0; i < 32; i++)
        {
            Assert.True(channel.Writer.TryWrite(i));
        }

        for (var i = 0; i < 32; i++)
        {
            Assert.True(channel.Reader.TryRead(out var value));
            Assert.Equal(i, value);
        }
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithUnboundedOptionsNull_Throws() =>
        Assert.Throws<ArgumentNullException>(static () => MakeChannel<int>((UnboundedChannelOptions)null!));

    [Fact(Timeout = 15_000)]
    public async Task MakeChannel_WithPrioritizedOptions_UsesDefaultPriority()
    {
        var options = new PrioritizedChannelOptions
        {
            PriorityLevels = 3,
            DefaultPriority = 2
        };

        var channel = MakeChannel<int>(options);

        await channel.PrioritizedWriter.WriteAsync(99, TestContext.Current.CancellationToken);
        await channel.PrioritizedWriter.WriteAsync(42, priority: 0, TestContext.Current.CancellationToken);
        channel.PrioritizedWriter.TryComplete();

        var first = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(42, first);
        Assert.Equal(99, second);
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithPrioritizedOptionsNull_Throws() =>
        Assert.Throws<ArgumentNullException>(static () => MakeChannel<int>((PrioritizedChannelOptions)null!));

    [Fact(Timeout = 15_000)]
    public async Task MakePrioritizedChannel_ConfiguresOptions()
    {
        var channel = MakePrioritizedChannel<int>(
            priorityLevels: 3,
            capacityPerLevel: 2,
            fullMode: BoundedChannelFullMode.Wait,
            singleReader: true,
            singleWriter: true,
            defaultPriority: 1);

        await channel.PrioritizedWriter.WriteAsync(100, TestContext.Current.CancellationToken);
        await channel.PrioritizedWriter.WriteAsync(200, priority: 0, TestContext.Current.CancellationToken);
        channel.PrioritizedWriter.TryComplete();

        var first = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(200, first);
        Assert.Equal(100, second);
    }

    [Fact(Timeout = 15_000)]
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

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_WithValueTaskContinuation_ShouldDrainAllCases()
    {
        var channel = MakeChannel<int>();
        var observed = new ConcurrentBag<int>();

        Task<Result<Unit>> fanInTask = SelectFanInValueTaskAsync(
            [channel.Reader],
            async (int value, CancellationToken ct) =>
            {
                await Task.Yield();
                observed.Add(value);
                return Result.Ok(Unit.Value);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await channel.Writer.WriteAsync(42, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await fanInTask;

        Assert.True(result.IsSuccess);
        Assert.Single(observed);
        Assert.Contains(42, observed);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_ShouldPropagateCaseFailure()
    {
        var channel = MakeChannel<int>();
        var fanInTask = SelectFanInAsync(
            [channel.Reader],
            static (int _, CancellationToken _) => Task.FromResult(Result.Fail<Unit>(Error.From("boom", ErrorCodes.Validation))),
            cancellationToken: TestContext.Current.CancellationToken);

        await channel.Writer.WriteAsync(5, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await fanInTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Equal("boom", result.Error?.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_ShouldRespectCancellation()
    {
        var channel = MakeChannel<int>();
        using var cts = new CancellationTokenSource(25);

        var result = await SelectFanInAsync(
            [channel.Reader],
            static (int _, CancellationToken _) => Task.FromResult(Result.Ok(Unit.Value)),
            cancellationToken: cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        channel.Writer.TryComplete();
    }

    [Fact(Timeout = 15_000)]
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
        }, TestContext.Current.CancellationToken);

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

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_WithNullReaders_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(static async () => await SelectFanInAsync<object>(null!, static (_, _) => Task.FromResult(Result.Ok(Unit.Value)), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_WithNullDelegate_Throws()
    {
        var channel = MakeChannel<int>();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, CancellationToken, Task<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, Task<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, CancellationToken, ValueTask<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, ValueTask<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, CancellationToken, Task>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, Task>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, CancellationToken, ValueTask>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, ValueTask>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Action<int>)null!, cancellationToken: TestContext.Current.CancellationToken));
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

        async Task ProduceAsync(ChannelWriter<int> writer, int value)
        {
            await writer.WriteAsync(value, TestContext.Current.CancellationToken);
            writer.TryComplete();
        }

        await Task.WhenAll(
            ProduceAsync(source1.Writer, expected[0]),
            ProduceAsync(source2.Writer, expected[^1]));
        var result = await fanInTask;

        Assert.True(result.IsSuccess);

        var observed = new List<int>();
        await foreach (var value in destination.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            observed.Add(value);
        }

        observed.Sort();
        Assert.Equal(expected, observed);
    }

    [Fact(Timeout = 15_000)]
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

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithEmptySources_CompletesDestinationWhenRequested()
    {
        var destination = MakeChannel<int>();

        var result = await FanInAsync(Array.Empty<ChannelReader<int>>(), destination.Writer, completeDestination: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(destination.Reader.Completion.IsCompleted);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithEmptySources_DoesNotCompleteWhenRequested()
    {
        var destination = MakeChannel<int>();

        var result = await FanInAsync(Array.Empty<ChannelReader<int>>(), destination.Writer, completeDestination: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(destination.Reader.Completion.IsCompleted);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithNullSources_Throws()
    {
        var destination = MakeChannel<int>();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await FanInAsync(null!, destination.Writer, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithNullDestination_Throws()
    {
        var source = MakeChannel<int>();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await FanInAsync([source.Reader], null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(new[] { 7, 9 })]
    public async Task FanIn_ShouldMergeSources(int[] expected)
    {
        var source1 = MakeChannel<int>();
        var source2 = MakeChannel<int>();

        var ct = TestContext.Current.CancellationToken;
        var merged = FanIn([source1.Reader, source2.Reader], cancellationToken: ct);

        async Task ProduceAsync(ChannelWriter<int> writer, int value)
        {
            await writer.WriteAsync(value, TestContext.Current.CancellationToken);
            writer.TryComplete();
        }

        await Task.WhenAll(
            ProduceAsync(source1.Writer, expected[0]),
            ProduceAsync(source2.Writer, expected[^1]));

        var values = new List<int>();
        await foreach (var value in merged.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            values.Add(value);
        }

        values.Sort();
        Assert.Equal(expected, values);
    }

    [Fact(Timeout = 15_000)]
    public void FanIn_WithNullSources_Throws() =>
        Assert.Throws<ArgumentNullException>(static () => FanIn<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNullSource_Throws()
    {
        var destination = MakeChannel<int>();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await FanOutAsync(null!, [destination.Writer], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNullDestinations_Throws()
    {
        var source = MakeChannel<int>();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await FanOutAsync(source.Reader, null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNullDestinationEntry_Throws()
    {
        var source = MakeChannel<int>();
        IReadOnlyList<ChannelWriter<int>> destinations = [null!];

        await Assert.ThrowsAsync<ArgumentException>(async () => await FanOutAsync(source.Reader, destinations, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNegativeDeadline_Throws()
    {
        var source = MakeChannel<int>();
        var destination = MakeChannel<int>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await FanOutAsync(source.Reader, [destination.Writer], deadline: TimeSpan.FromSeconds(-1), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNoDestinations_ReturnsSuccess()
    {
        var source = MakeChannel<int>();
        var result = await FanOutAsync(source.Reader, Array.Empty<ChannelWriter<int>>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact(Timeout = 15_000)]
    public void FanOut_WithNullSource_Throws() =>
        Assert.Throws<ArgumentNullException>(static () => FanOut<int>(null!, 1, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public void FanOut_WithInvalidBranchCount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(static () => FanOut(MakeChannel<int>().Reader, 0, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public void FanOut_ShouldUseChannelFactory()
    {
        var source = MakeChannel<int>();
        int invocationCount = 0;

        IReadOnlyList<ChannelReader<int>> branches = FanOut(
            source.Reader,
            branchCount: 2,
            cancellationToken: TestContext.Current.CancellationToken,
            channelFactory: index =>
            {
                invocationCount++;
                return BoundedChannel<int>(index + 1)
                    .AllowSynchronousContinuations()
                    .Build();
            });

        Assert.Equal(2, invocationCount);
        Assert.Equal(2, branches.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOut_WithCompleteBranchesFalse_DoesNotCompleteBranches()
    {
        var source = MakeChannel<int>();
        var branches = FanOut(source.Reader, branchCount: 1, completeBranches: false, cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var value = await branches[0].ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, value);
        Assert.False(branches[0].Completion.IsCompleted);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOut_WithCompleteBranchesTrue_CompletesReaders()
    {
        var source = MakeChannel<int>();
        var branches = FanOut(source.Reader, branchCount: 1, completeBranches: true, cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var value = await branches[0].ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, value);
        await branches[0].Completion;
    }

    [Fact(Timeout = 15_000)]
    public async Task FanIn_ShouldPropagateCancellation()
    {
        var source = MakeChannel<int>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var merged = FanIn([source.Reader], cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await merged.ReadAsync(cts.Token));
        source.Writer.TryComplete();
    }

    [Fact(Timeout = 15_000)]
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

    [Fact(Timeout = 15_000)]
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
        }, TestContext.Current.CancellationToken);

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

    [Fact(Timeout = 15_000)]
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

    [Fact(Timeout = 15_000)]
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
