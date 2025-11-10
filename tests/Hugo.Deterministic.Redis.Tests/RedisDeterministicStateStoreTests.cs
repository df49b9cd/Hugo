using Hugo;
using Hugo.Deterministic.Redis;

using StackExchange.Redis;

using Testcontainers.Redis;

using Xunit;

internal sealed class RedisDeterministicStateStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCleanUp(true)
        .Build();

    private IConnectionMultiplexer? _multiplexer;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString()).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.CloseAsync().ConfigureAwait(false);
            _multiplexer.Dispose();
        }

        await _container.DisposeAsync().ConfigureAwait(false);
    }

    private RedisDeterministicStateStore CreateStore() =>
        new(new RedisDeterministicStateStoreOptions
        {
            ConnectionMultiplexer = _multiplexer ?? throw new InvalidOperationException("Redis connection not initialised."),
            KeyPrefix = "hugo:test:"
        });

    [Fact]
    public void SetAndGetRoundTripsRecord()
    {
        RedisDeterministicStateStore store = CreateStore();

        DeterministicRecord record = new("workflow.test", 1, [1, 2, 3], DateTimeOffset.UtcNow);

        store.Set("roundtrip", record);

        Assert.True(store.TryGet("roundtrip", out DeterministicRecord stored));
        Assert.Equal(record.Kind, stored.Kind);
        Assert.Equal(record.Version, stored.Version);
        Assert.Equal(record.Payload.ToArray(), stored.Payload.ToArray());
    }

    [Fact]
    public void SetOverwritesExistingRecord()
    {
        RedisDeterministicStateStore store = CreateStore();

        DeterministicRecord first = new("workflow.test", 1, [1], DateTimeOffset.UtcNow);
        DeterministicRecord second = new("workflow.test", 2, [2], DateTimeOffset.UtcNow.AddMinutes(1));

        store.Set("update", first);
        store.Set("update", second);

        Assert.True(store.TryGet("update", out DeterministicRecord stored));
        Assert.Equal(2, stored.Version);
        Assert.Equal([2], stored.Payload.ToArray());
    }
}
