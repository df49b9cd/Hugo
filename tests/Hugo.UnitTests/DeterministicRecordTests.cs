using Shouldly;
namespace Hugo.Tests;

public class DeterministicRecordTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_ShouldThrow_WhenKindMissing(string kind)
    {
        var payload = new byte[] { 1 };

        Should.Throw<ArgumentException>(() => new DeterministicRecord(kind, 1, payload, DateTimeOffset.UtcNow));
    }

    [Fact(Timeout = 15_000)]
    public void Constructor_ShouldThrow_WhenKindNull()
    {
        var payload = new byte[] { 1 };

        Should.Throw<ArgumentException>(() => new DeterministicRecord(null!, 1, payload, DateTimeOffset.UtcNow));
    }

    [Fact(Timeout = 15_000)]
    public void Payload_ShouldBeCloned()
    {
        var payload = new byte[] { 1, 2, 3 };
        var record = new DeterministicRecord("hugo.test", 1, payload, DateTimeOffset.UtcNow);

        payload[0] = 42;

        record.Payload.Span[0].ShouldBe((byte)1);
    }
}
