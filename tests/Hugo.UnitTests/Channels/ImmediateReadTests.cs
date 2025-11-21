using Hugo;


namespace Hugo.Tests.Channels;

public sealed class ImmediateReadTests
{
    [Fact(Timeout = 15_000)]
    public void ImmediateRead_ShouldExposeValue()
    {
        var read = new ImmediateRead<int>(7);

        read.Value.ShouldBe(7);
    }
}
