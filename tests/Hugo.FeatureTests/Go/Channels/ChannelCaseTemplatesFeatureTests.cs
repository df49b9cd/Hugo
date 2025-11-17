using Hugo;
using System.Threading.Channels;
using Shouldly;

using static Hugo.Go;

namespace Hugo.Tests;

public sealed class ChannelCaseTemplatesFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task WithMultipleTemplates_ShouldMaterializeCasesAndSelect()
    {
        var empty = Channel.CreateUnbounded<int>();
        var ready = Channel.CreateUnbounded<int>();

        await ready.Writer.WriteAsync(7, TestContext.Current.CancellationToken);
        ready.Writer.TryComplete();

        ChannelCaseTemplate<int>[] templates =
        [
            ChannelCaseTemplates.From(empty.Reader),
            ChannelCaseTemplates.From(ready.Reader)
        ];

        ChannelCase<int>[] cases = templates
            .Select(template => template.With<int>((value, _) => ValueTask.FromResult(Result.Ok(value))))
            .ToArray();

        var result = await SelectAsync(cases: cases, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(7);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldPrioritizeImmediateReadyCase()
    {
        var idle = Channel.CreateUnbounded<string>();
        var ready = Channel.CreateUnbounded<string>();

        await ready.Writer.WriteAsync("ready", TestContext.Current.CancellationToken);

        ChannelCase<string>[] cases =
        [
            ChannelCase.Create(idle.Reader, (value, _) => ValueTask.FromResult(Result.Ok(value))),
            ChannelCase.Create(ready.Reader, (value, _) => ValueTask.FromResult(Result.Ok(value)))
        ];

        Result<string> result = await SelectAsync(cases: cases, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("ready");
        idle.Reader.TryRead(out _).ShouldBeFalse();
    }
}
