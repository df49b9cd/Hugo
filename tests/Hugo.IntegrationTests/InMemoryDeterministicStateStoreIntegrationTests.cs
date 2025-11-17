using Shouldly;

namespace Hugo.Tests;

public sealed class InMemoryDeterministicStateStoreIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public void SetAndTryGet_ShouldRoundTripRecord()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("kind", version: 1, payload: ReadOnlyMemory<byte>.Empty.Span, recordedAt: DateTimeOffset.UtcNow);

        store.Set("key", record);

        store.TryGet("key", out var retrieved).ShouldBeTrue();
        retrieved.Kind.ShouldBe("kind");
        retrieved.Version.ShouldBe(1);
    }
}
