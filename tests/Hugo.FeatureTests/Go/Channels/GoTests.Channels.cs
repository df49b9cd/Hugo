using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Shouldly;

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

        channel.Writer.TryWrite(1).ShouldBeTrue();
        channel.Writer.TryWrite(2).ShouldBeTrue();
        channel.Writer.TryWrite(3).ShouldBeTrue();

        channel.Reader.TryRead(out var first).ShouldBeTrue();
        first.ShouldBe(2);
        channel.Reader.TryRead(out var second).ShouldBeTrue();
        second.ShouldBe(3);
        channel.Reader.TryRead(out _).ShouldBeFalse();
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

        channel.ShouldNotBeNull();
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.AllowSynchronousContinuations.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_ShouldApplyUnboundedBuilderDelegate()
    {
        UnboundedChannelOptions? capturedOptions = null;

        Channel<int> channel = MakeChannel<int>(
            configureUnbounded: builder => builder
                .AllowSynchronousContinuations()
                .Configure(options => capturedOptions = options));

        channel.ShouldNotBeNull();
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.AllowSynchronousContinuations.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithZeroCapacity_UsesUnboundedConfiguration()
    {
        var channel = MakeChannel<int>(capacity: 0, singleReader: true, singleWriter: true);

        for (var i = 0; i < 64; i++)
        {
            channel.Writer.TryWrite(i).ShouldBeTrue();
        }

        for (var i = 0; i < 64; i++)
        {
            channel.Reader.TryRead(out var value).ShouldBeTrue();
            value.ShouldBe(i);
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

        channel.Writer.TryWrite(10).ShouldBeTrue();
        channel.Writer.TryWrite(20).ShouldBeTrue();
        channel.Reader.TryRead(out var value).ShouldBeTrue();
        value.ShouldBe(20);
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithBoundedOptionsNull_Throws() =>
        Should.Throw<ArgumentNullException>(static () => MakeChannel<int>((BoundedChannelOptions)null!));

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
            channel.Writer.TryWrite(i).ShouldBeTrue();
        }

        for (var i = 0; i < 32; i++)
        {
            channel.Reader.TryRead(out var value).ShouldBeTrue();
            value.ShouldBe(i);
        }
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithUnboundedOptionsNull_Throws() =>
        Should.Throw<ArgumentNullException>(static () => MakeChannel<int>((UnboundedChannelOptions)null!));

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

        first.ShouldBe(42);
        second.ShouldBe(99);
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithPrioritizedOptionsNull_Throws() =>
        Should.Throw<ArgumentNullException>(static () => MakeChannel<int>((PrioritizedChannelOptions)null!));

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

        first.ShouldBe(200);
        second.ShouldBe(100);
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

        result.IsSuccess.ShouldBeTrue();
        observed.ShouldContain(11);
        observed.ShouldContain(22);
        observed.Count.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_WithValueTaskContinuation_ShouldDrainAllCases()
    {
        var channel = MakeChannel<int>();
        var observed = new ConcurrentBag<int>();

        ValueTask<Result<Unit>> fanInTask = SelectFanInValueTaskAsync(
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

        result.IsSuccess.ShouldBeTrue();
        observed.ShouldHaveSingleItem();
        observed.ShouldContain(42);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
        result.Error?.Message.ShouldBe("boom");
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
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
            await advanceCts.CancelAsync();
            await advanceLoop;
        }

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Timeout);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_WithNullReaders_Throws() => await Should.ThrowAsync<ArgumentNullException>(static async () => await SelectFanInAsync<object>(null!, static (_, _) => Task.FromResult(Result.Ok(Unit.Value)), cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task SelectFanInAsync_WithNullDelegate_Throws()
    {
        var channel = MakeChannel<int>();

        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, CancellationToken, Task<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, Task<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, CancellationToken, ValueTask<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, ValueTask<Result<Unit>>>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, CancellationToken, Task>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Func<int, Task>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, CancellationToken, ValueTask>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInValueTaskAsync([channel.Reader], (Func<int, ValueTask>)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(async () => await SelectFanInAsync([channel.Reader], (Action<int>)null!, cancellationToken: TestContext.Current.CancellationToken));
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

        result.IsSuccess.ShouldBeTrue();

        var observed = new List<int>();
        await foreach (var value in destination.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            observed.Add(value);
        }

        observed.Sort();
        observed.ShouldBe(expected);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        await Should.ThrowAsync<InvalidOperationException>(async () => await destination.Reader.Completion);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithEmptySources_CompletesDestinationWhenRequested()
    {
        var destination = MakeChannel<int>();

        var result = await FanInAsync([], destination.Writer, completeDestination: true, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        destination.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithEmptySources_DoesNotCompleteWhenRequested()
    {
        var destination = MakeChannel<int>();

        var result = await FanInAsync([], destination.Writer, completeDestination: false, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        destination.Reader.Completion.IsCompleted.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithNullSources_Throws()
    {
        var destination = MakeChannel<int>();
        await Should.ThrowAsync<ArgumentNullException>(async () => await FanInAsync(null!, destination.Writer, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_WithNullDestination_Throws()
    {
        var source = MakeChannel<int>();
        await Should.ThrowAsync<ArgumentNullException>(async () => await FanInAsync([source.Reader], null!, cancellationToken: TestContext.Current.CancellationToken));
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
        values.ShouldBe(expected);
    }

    [Fact(Timeout = 15_000)]
    public void FanIn_WithNullSources_Throws() =>
        Should.Throw<ArgumentNullException>(static () => FanIn<int>(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNullSource_Throws()
    {
        var destination = MakeChannel<int>();
        await Should.ThrowAsync<ArgumentNullException>(async () => await FanOutAsync(null!, [destination.Writer], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNullDestinations_Throws()
    {
        var source = MakeChannel<int>();
        await Should.ThrowAsync<ArgumentNullException>(async () => await FanOutAsync(source.Reader, null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNullDestinationEntry_Throws()
    {
        var source = MakeChannel<int>();
        IReadOnlyList<ChannelWriter<int>> destinations = [null!];

        await Should.ThrowAsync<ArgumentException>(async () => await FanOutAsync(source.Reader, destinations, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNegativeDeadline_Throws()
    {
        var source = MakeChannel<int>();
        var destination = MakeChannel<int>();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await FanOutAsync(source.Reader, [destination.Writer], deadline: TimeSpan.FromSeconds(-1), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_WithNoDestinations_ReturnsSuccess()
    {
        var source = MakeChannel<int>();
        var result = await FanOutAsync(source.Reader, [], cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public void FanOut_WithNullSource_Throws() =>
        Should.Throw<ArgumentNullException>(static () => FanOut<int>(null!, 1, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public void FanOut_WithInvalidBranchCount_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(static () => FanOut(MakeChannel<int>().Reader, 0, cancellationToken: TestContext.Current.CancellationToken));

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

        invocationCount.ShouldBe(2);
        branches.Count.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOut_WithCompleteBranchesFalse_DoesNotCompleteBranches()
    {
        var source = MakeChannel<int>();
        var branches = FanOut(source.Reader, branchCount: 1, completeBranches: false, cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var value = await branches[0].ReadAsync(TestContext.Current.CancellationToken);
        value.ShouldBe(1);
        branches[0].Completion.IsCompleted.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOut_WithCompleteBranchesTrue_CompletesReaders()
    {
        var source = MakeChannel<int>();
        var branches = FanOut(source.Reader, branchCount: 1, completeBranches: true, cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var value = await branches[0].ReadAsync(TestContext.Current.CancellationToken);
        value.ShouldBe(1);
        await branches[0].Completion;
    }

    [Fact(Timeout = 15_000)]
    public async Task FanIn_ShouldPropagateCancellation()
    {
        var source = MakeChannel<int>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var merged = FanIn([source.Reader], cancellationToken: cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () => await merged.ReadAsync(cts.Token));
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

        result.IsSuccess.ShouldBeTrue();
        (await destination1.Reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(17);
        (await destination2.Reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(17);
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
        destination.Writer.TryWrite(7).ShouldBeTrue();

        Task<Result<Unit>> fanOutTask = FanOutAsync(
            source.Reader,
            [destination.Writer],
            deadline: TimeSpan.FromSeconds(1),
            provider: provider,
            cancellationToken: TestContext.Current.CancellationToken).AsTask();

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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Timeout);
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

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
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

        value1.ShouldBe(99);
        value2.ShouldBe(99);
        await Should.ThrowAsync<ChannelClosedException>(async () => await branches[0].ReadAsync(TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ChannelClosedException>(async () => await branches[1].ReadAsync(TestContext.Current.CancellationToken));
    }
}
