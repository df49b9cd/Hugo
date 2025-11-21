using System.Threading.Channels;

using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualBasic;


namespace Hugo.Tests;

public class WindowPipelineIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task WindowAsync_ShouldFlushAfterTimerThenAcceptLaterData()
    {
        var provider = new FakeTimeProvider();
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("window-integration", scope, provider, TestContext.Current.CancellationToken);
        var source = Channel.CreateUnbounded<int>();

        var reader = await ResultPipelineChannels.WindowAsync(
            context,
            source.Reader,
            batchSize: 3,
            flushInterval: TimeSpan.FromSeconds(2),
            cancellationToken: TestContext.Current.CancellationToken);

        // Trigger timer first with no data.
        provider.Advance(TimeSpan.FromSeconds(2));

        // Now send data that should still be batched correctly.
        await source.Writer.WriteAsync(10, TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(11, TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(12, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var batches = new List<IReadOnlyList<int>>();
        while (await reader.WaitToReadAsync(TestContext.Current.CancellationToken))
        {
            while (reader.TryRead(out var batch))
            {
                batches.Add(batch);
            }
        }

        batches.ShouldHaveSingleItem();
        batches[0].ShouldBe([10, 11, 12]);
    }
}
