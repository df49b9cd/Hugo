using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests;

public sealed class WaitGroupTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldReturnFalse_WhenTimeoutExpiresBeforeCompletion()
    {
        var provider = new FakeTimeProvider();
        var waitGroup = new WaitGroup();
        waitGroup.Add(1);

        ValueTask<bool> completion = waitGroup.WaitAsync(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(5));

        (await completion).ShouldBeFalse();
        waitGroup.Count.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldReturnTrue_WhenWorkCompletesBeforeTimeout()
    {
        var provider = new FakeTimeProvider();
        var waitGroup = new WaitGroup();
        waitGroup.Add(1);

        ValueTask<bool> completion = waitGroup.WaitAsync(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        waitGroup.Done();
        var finishedBeforeTimeout = await completion;

        finishedBeforeTimeout.ShouldBeTrue();
        waitGroup.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Add_ShouldThrow_WhenDeltaIsNonPositive(int delta)
    {
        var waitGroup = new WaitGroup();

        Should.Throw<ArgumentOutOfRangeException>(() => waitGroup.Add(delta));
        waitGroup.Count.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldThrow_WhenCancellationAlreadyRequested()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var waitGroup = new WaitGroup();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waitGroup.WaitAsync(cts.Token));
        waitGroup.Count.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public void Done_ShouldThrow_WhenCounterBecomesNegative()
    {
        var waitGroup = new WaitGroup();

        Should.Throw<InvalidOperationException>(() => waitGroup.Done());
        waitGroup.Count.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Go_ShouldReleaseCount_WhenWorkFaults()
    {
        var waitGroup = new WaitGroup();

        waitGroup.Go(static async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }, cancellationToken: TestContext.Current.CancellationToken);

        await waitGroup.WaitAsync(TestContext.Current.CancellationToken);
        waitGroup.Count.ShouldBe(0);
    }
}
