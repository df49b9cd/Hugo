namespace Hugo.Tests;

public class InMemoryDeterministicStateStoreTests
{
    [Fact(Timeout = 15_000)]
    public void TryGet_ShouldThrow_WhenKeyMissing()
    {
        var store = new InMemoryDeterministicStateStore();

        Assert.Throws<ArgumentException>(() => store.TryGet("", out _));
    }

    [Fact(Timeout = 15_000)]
    public void Set_ShouldThrow_WhenKeyMissing()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("hugo.test", 1, [], DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => store.Set(" ", record));
    }

    [Fact(Timeout = 15_000)]
    public void Set_ShouldThrow_WhenRecordNull()
    {
        var store = new InMemoryDeterministicStateStore();

        Assert.Throws<ArgumentNullException>(() => store.Set("key", null!));
    }

    [Fact(Timeout = 15_000)]
    public void SetAndTryGet_ShouldReturnStoredRecord()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("hugo.test", 2, [1], DateTimeOffset.UtcNow);

        store.Set("key", record);

        Assert.True(store.TryGet("key", out var retrieved));
        Assert.Same(record, retrieved);
    }
}
