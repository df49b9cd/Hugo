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

    [Fact(Timeout = 15_000)]
    public void TryAdd_ShouldNotOverwriteExistingRecord()
    {
        var store = new InMemoryDeterministicStateStore();
        var original = new DeterministicRecord("kind", version: 1, payload: ReadOnlyMemory<byte>.Empty.Span, recordedAt: DateTimeOffset.UtcNow);
        var replacement = new DeterministicRecord("kind", version: 2, payload: ReadOnlyMemory<byte>.Empty.Span, recordedAt: DateTimeOffset.UtcNow);

        store.Set("key", original);
        store.TryAdd("key", replacement).ShouldBeFalse();

        store.TryGet("key", out var retrieved).ShouldBeTrue();
        retrieved.Version.ShouldBe(original.Version);
    }

    [Fact(Timeout = 15_000)]
    public void Set_ShouldThrow_WhenRecordIsNull()
    {
        var store = new InMemoryDeterministicStateStore();

        Should.Throw<ArgumentNullException>(() => store.Set("key", null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void StoreOperations_ShouldValidateKey(string? key)
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("kind", version: 1, payload: ReadOnlyMemory<byte>.Empty.Span, recordedAt: DateTimeOffset.UtcNow);

        Should.Throw<ArgumentException>(() => store.Set(key!, record));
        Should.Throw<ArgumentException>(() => store.TryGet(key!, out _));
        Should.Throw<ArgumentException>(() => store.TryAdd(key!, record));
    }
}
