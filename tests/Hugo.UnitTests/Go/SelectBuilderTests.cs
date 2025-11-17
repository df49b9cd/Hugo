using System.Threading.Channels;
using Shouldly;

namespace Hugo.Tests.Selecting;

public sealed class SelectBuilderTests
{
    [Fact(Timeout = 15_000)]
    public async Task ExecuteAsync_ShouldThrow_WhenNoCasesRegistered()
    {
        var builder = global::Hugo.Go.Select<int>(cancellationToken: TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () => await builder.ExecuteAsync());
    }

    [Fact(Timeout = 15_000)]
    public async Task ExecuteAsync_ShouldUseDefaultCase()
    {
        var builder = global::Hugo.Go.Select<int>(cancellationToken: TestContext.Current.CancellationToken)
            .Default(() => 99);

        var result = await builder.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(99);
    }

    [Fact(Timeout = 15_000)]
    public async Task Deadline_ShouldCompleteWithTimeoutResult()
    {
        var builder = global::Hugo.Go.Select<string>(timeout: TimeSpan.FromMilliseconds(10), cancellationToken: TestContext.Current.CancellationToken)
            .Deadline(TimeSpan.FromMilliseconds(5), () => Task.FromResult(Result.Ok("deadline")));

        var result = await builder.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("deadline");
    }
}
