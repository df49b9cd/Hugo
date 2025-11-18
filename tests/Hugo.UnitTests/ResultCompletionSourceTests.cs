using Shouldly;

namespace Hugo.Tests;

public sealed class ResultCompletionSourceTests
{
    [Fact(Timeout = 5_000)]
    public async ValueTask TrySetCanceled_ShouldPropagateCancellation()
    {
        var source = new ResultCompletionSource<int>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        source.TrySetCanceled(cts.Token).ShouldBeTrue();

        var canceled = await Should.ThrowAsync<TaskCanceledException>(async () => await source.ValueTask);
        canceled.CancellationToken.ShouldBe(cts.Token);
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask TrySetException_ShouldPropagateException()
    {
        var source = new ResultCompletionSource<int>();
        var boom = new InvalidOperationException("boom");

        source.TrySetException(boom).ShouldBeTrue();

        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () => await source.ValueTask);
        thrown.ShouldBeSameAs(boom);
    }
}
