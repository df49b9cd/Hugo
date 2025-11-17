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
}
