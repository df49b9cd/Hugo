namespace Hugo.Tests;

public class InMemoryDeterministicStateStoreTests
{
    [Fact(Timeout = 15_000)]
    public void TryGet_ShouldThrow_WhenKeyMissing()
    {
        var store = new InMemoryDeterministicStateStore();

        Should.Throw<ArgumentException>(() => store.TryGet("", out _));
    }

    [Fact(Timeout = 15_000)]
    public void Set_ShouldThrow_WhenKeyMissing()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("hugo.test", 1, [], DateTimeOffset.UtcNow);

        Should.Throw<ArgumentException>(() => store.Set(" ", record));
    }

    [Fact(Timeout = 15_000)]
    public void Set_ShouldThrow_WhenRecordNull()
    {
        var store = new InMemoryDeterministicStateStore();

        Should.Throw<ArgumentNullException>(() => store.Set("key", null!));
    }

    [Fact(Timeout = 15_000)]
    public void SetAndTryGet_ShouldReturnStoredRecord()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("hugo.test", 2, [1], DateTimeOffset.UtcNow);

        store.Set("key", record);

        store.TryGet("key", out var retrieved).ShouldBeTrue();
        retrieved.ShouldBeSameAs(record);
    }
}
