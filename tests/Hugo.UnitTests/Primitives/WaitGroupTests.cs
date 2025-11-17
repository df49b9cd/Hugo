using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Hugo.Tests;

public sealed class WaitGroupTests
{
    [Fact(Timeout = 15_000)]
    public async Task WaitAsync_ShouldReturnFalse_WhenTimeoutExpiresBeforeCompletion()
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
    public async Task WaitAsync_ShouldReturnTrue_WhenWorkCompletesBeforeTimeout()
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
}
