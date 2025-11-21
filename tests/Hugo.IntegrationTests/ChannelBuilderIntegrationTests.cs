using System.Threading.Channels;

using Hugo;


namespace Hugo.IntegrationTests;

public sealed class ChannelBuilderIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task UnboundedChannelBuilder_ShouldApplyConfigurationAcrossBuilds()
    {
        int configureCount = 0;

        var builder = new UnboundedChannelBuilder<string>()
            .SingleReader()
            .SingleWriter()
            .AllowSynchronousContinuations()
            .Configure(options =>
            {
                configureCount++;
                options.SingleReader.ShouldBeTrue();
                options.SingleWriter.ShouldBeTrue();
                options.AllowSynchronousContinuations.ShouldBeTrue();
            });

        Channel<string> first = builder.Build();
        Channel<string> second = builder.Build();

        configureCount.ShouldBe(2);

        await first.Writer.WriteAsync("first", TestContext.Current.CancellationToken);
        await second.Writer.WriteAsync("second", TestContext.Current.CancellationToken);

        (await first.Reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe("first");
        (await second.Reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe("second");
    }
}
