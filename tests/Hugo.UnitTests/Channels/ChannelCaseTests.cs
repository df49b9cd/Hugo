using System.Threading.Channels;

using Shouldly;

namespace Hugo.Tests.Channels;

public sealed class ChannelCaseTests
{
    [Fact(Timeout = 15_000)]
    public async Task TryDequeueImmediately_ShouldContinueWithReadyValue()
    {
        var channel = Channel.CreateBounded<int>(1);
        channel.Writer.TryWrite(7);

        ChannelCase<int> channelCase = ChannelCase.Create(channel.Reader, (value, _) => ValueTask.FromResult(Result.Ok(value * 2)));

        var dequeued = channelCase.TryDequeueImmediately(out var state);

        dequeued.ShouldBeTrue();

        var result = await channelCase.ContinueWithAsync(state, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(14);
    }

    [Fact(Timeout = 15_000)]
    public async Task CreateDefault_ShouldSurfaceFailure_WhenCallbackThrows()
    {
        ChannelCase<int> channelCase = ChannelCase<int>.CreateDefault(_ => throw new InvalidOperationException("default-failure"));

        var result = await channelCase.ContinueWithAsync(null, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldContain("default-failure");
    }

    [Fact(Timeout = 15_000)]
    public async Task CreateOverloads_ShouldExecuteContinuations()
    {
        async Task<int> ExecuteAsync(ChannelCase<int> @case)
        {
            var wait = await @case.WaitAsync(TestContext.Current.CancellationToken);
            wait.HasValue.ShouldBeTrue();
            var outcome = await @case.ContinueWithAsync(wait.Value, TestContext.Current.CancellationToken);
            outcome.IsSuccess.ShouldBeTrue();
            return outcome.Value;
        }

        async Task<int> RunWithCase(Func<ChannelReader<int>, ChannelCase<int>> factory, int payload)
        {
            var channel = Channel.CreateBounded<int>(1);
            channel.Writer.TryWrite(payload);
            return await ExecuteAsync(factory(channel.Reader));
        }

        var valueTaskResult = await RunWithCase(reader => ChannelCase.Create(reader, (int value) => ValueTask.FromResult(Result.Ok(value + 1))), 1);
        var cancellationAware = await RunWithCase(reader => ChannelCase.Create(reader, (int value, CancellationToken _) => ValueTask.FromResult(Result.Ok(value + 2))), 2);
        var valueProducer = await RunWithCase(reader => ChannelCase.Create(reader, (int value, CancellationToken _) => ValueTask.FromResult(value + 3)), 3);
        var valueProducerNoToken = await RunWithCase(reader => ChannelCase.Create(reader, (int value) => ValueTask.FromResult(value + 4)), 4);
        var noResult = await RunWithCase(reader => ChannelCase.Create<int, int>(reader, (int _) => ValueTask.CompletedTask), 5);

        valueTaskResult.ShouldBe(2);
        cancellationAware.ShouldBe(4);
        valueProducer.ShouldBe(6);
        valueProducerNoToken.ShouldBe(8);
        noResult.ShouldBe(0); // default(TResult) for void callbacks
    }

    [Fact(Timeout = 15_000)]
    public async Task CreateDefaultOverloads_ShouldHonorPriorityAndReturnResults()
    {
        var fromValueTask = ChannelCase.CreateDefault<int>(() => ValueTask.FromResult(Result.Ok(10)), priority: 2);
        var fromToken = ChannelCase.CreateDefault<int>(_ => ValueTask.FromResult(Result.Ok(11)), priority: 1);
        var fromSync = ChannelCase.CreateDefault<int>(() => Result.Ok(12), priority: 0);

        (await fromValueTask.WaitAsync(TestContext.Current.CancellationToken)).HasValue.ShouldBeTrue();
        (await fromToken.WaitAsync(TestContext.Current.CancellationToken)).HasValue.ShouldBeTrue();
        (await fromSync.WaitAsync(TestContext.Current.CancellationToken)).HasValue.ShouldBeTrue();

        var values = new[]
        {
            (await fromValueTask.ContinueWithAsync(null, TestContext.Current.CancellationToken)).Value,
            (await fromToken.ContinueWithAsync(null, TestContext.Current.CancellationToken)).Value,
            (await fromSync.ContinueWithAsync(null, TestContext.Current.CancellationToken)).Value
        };

        values.ShouldBe(new[] { 10, 11, 12 });
        fromValueTask.WithPriority(5).Priority.ShouldBe(5);
    }
}
