using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Hugo.Tests;

[Collection(TestCollections.DiagnosticsIsolation)]
public class TimeProviderDelayTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldReturnTrue_WhenDueTimeIsNonPositive()
    {
        var provider = new FakeTimeProvider();

        (await TimeProviderDelay.WaitAsync(provider, TimeSpan.Zero, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await TimeProviderDelay.WaitAsync(provider, TimeSpan.FromMilliseconds(-5), TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldCompleteAfterProviderAdvances()
    {
        var provider = new FakeTimeProvider();
        var delay = TimeProviderDelay.WaitAsync(provider, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        delay.IsCompleted.ShouldBeFalse();

        provider.Advance(TimeSpan.FromSeconds(1));

        (await delay).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldHonorCancellationWhilePending()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        ValueTask<bool> delay = TimeProviderDelay.WaitAsync(provider, TimeSpan.FromSeconds(5), cts.Token);

        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => delay.AsTask());
    }
}
