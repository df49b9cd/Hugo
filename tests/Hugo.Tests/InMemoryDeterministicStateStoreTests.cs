namespace Hugo.Tests;

public class InMemoryDeterministicStateStoreTests
{
    [Fact]
    public void TryGet_ShouldThrow_WhenKeyMissing()
    {
        var store = new InMemoryDeterministicStateStore();

        Assert.Throws<ArgumentException>(() => store.TryGet("", out _));
    }

    [Fact]
    public void Set_ShouldThrow_WhenKeyMissing()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("hugo.test", 1, Array.Empty<byte>(), DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => store.Set(" ", record));
    }

    [Fact]
    public void Set_ShouldThrow_WhenRecordNull()
    {
        var store = new InMemoryDeterministicStateStore();

        Assert.Throws<ArgumentNullException>(() => store.Set("key", null!));
    }

    [Fact]
    public void SetAndTryGet_ShouldReturnStoredRecord()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("hugo.test", 2, new byte[] { 1 }, DateTimeOffset.UtcNow);

        store.Set("key", record);

        Assert.True(store.TryGet("key", out var retrieved));
        Assert.Same(record, retrieved);
    }
}
