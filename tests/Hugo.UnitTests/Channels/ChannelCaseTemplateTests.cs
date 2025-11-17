using System.Linq;
using System.Threading.Channels;
using Hugo;
using Shouldly;
using Unit = Hugo.Go.Unit;

namespace Hugo.Tests.Channels;

public sealed class ChannelCaseTemplateTests
{
    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldConsumeImmediateReadyValue_WithTemplate()
    {
        var channel = Channel.CreateBounded<int>(1);
        channel.Writer.TryWrite(42);

        var template = ChannelCaseTemplates.From(channel.Reader);
        ChannelCase<int>[] cases = [template.With<int>((value, _) => ValueTask.FromResult(Result.Ok(value)))];

        var result = await Go.SelectAsync(cases: cases, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldHonorCasePriority()
    {
        var high = Channel.CreateBounded<string>(1);
        var low = Channel.CreateBounded<string>(1);
        high.Writer.TryWrite("high");

        ChannelCase<string> highCase = ChannelCase.Create(high.Reader, (_, _) => ValueTask.FromResult(Result.Ok("high"))).WithPriority(5);
        ChannelCase<string> lowCase = ChannelCase.Create(low.Reader, (_, _) => ValueTask.FromResult(Result.Ok("low"))).WithPriority(1);

        var result = await Go.SelectAsync(
            provider: null,
            cancellationToken: TestContext.Current.CancellationToken,
            highCase);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("high");
    }

    [Fact(Timeout = 15_000)]
    public async Task Template_WithAction_ShouldExecuteAndReturnDefaultResult()
    {
        var channel = Channel.CreateBounded<int>(1);
        channel.Writer.TryWrite(7);

        int observed = 0;
        var template = ChannelCaseTemplates.From(channel.Reader);
        ChannelCase<Unit> case1 = template.With<Unit>((value) => observed = value);

        var result = await Go.SelectAsync(cases: new[] { case1 }, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        observed.ShouldBe(7);
    }

    [Fact(Timeout = 15_000)]
    public async Task TemplatesExtension_WithValueTaskSelector_ShouldMaterializeAllCases()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();

        await second.Writer.WriteAsync(11, TestContext.Current.CancellationToken);

        IEnumerable<ChannelCaseTemplate<int>> templates =
            [ChannelCaseTemplates.From(first.Reader), ChannelCaseTemplates.From(second.Reader)];

        ChannelCase<int>[] cases = [.. templates.Select(template => template.With<int>((value, _) => ValueTask.FromResult(Result.Ok(value))))];

        var result = await Go.SelectAsync(cases: cases, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(11);
    }
}
