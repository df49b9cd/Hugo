using Hugo;
using Hugo.Deterministic.Redis;


using StackExchange.Redis;

using Testcontainers.Redis;

public sealed class RedisDeterministicStateStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCleanUp(true)
        .Build();

    private IConnectionMultiplexer? _multiplexer;
    private bool _skip;
    private string? _skipReason;

    public async ValueTask InitializeAsync()
    {
        if (DeterministicTestSkipper.ShouldSkip(out string? reason))
        {
            _skip = true;
            _skipReason = reason;
            return;
        }

        try
        {
            await _container.StartAsync().ConfigureAwait(false);
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _skip = true;
            _skipReason = $"Redis container unavailable: {ex.Message}";
        }
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

    [Fact(Timeout = 60_000)]
    public void SetAndGetRoundTripsRecord()
    {
        if (SkipIfNecessary())
        {
            return;
        }

        RedisDeterministicStateStore store = CreateStore();

        DeterministicRecord record = new("workflow.test", 1, [1, 2, 3], DateTimeOffset.UtcNow);

        store.Set("roundtrip", record);

        store.TryGet("roundtrip", out DeterministicRecord stored).ShouldBeTrue();
        stored.Kind.ShouldBe(record.Kind);
        stored.Version.ShouldBe(record.Version);
        stored.Payload.ToArray().ShouldBe(record.Payload.ToArray());
    }

    [Fact(Timeout = 60_000)]
    public void SetOverwritesExistingRecord()
    {
        if (SkipIfNecessary())
        {
            return;
        }

        RedisDeterministicStateStore store = CreateStore();

        DeterministicRecord first = new("workflow.test", 1, [1], DateTimeOffset.UtcNow);
        DeterministicRecord second = new("workflow.test", 2, [2], DateTimeOffset.UtcNow.AddMinutes(1));

        store.Set("update", first);
        store.Set("update", second);

        store.TryGet("update", out DeterministicRecord stored).ShouldBeTrue();
        stored.Version.ShouldBe(2);
        stored.Payload.ToArray().ShouldBe([2]);
    }
    private bool SkipIfNecessary()
    {
        if (_skip)
        {
            if (!string.IsNullOrWhiteSpace(_skipReason))
            {
                Console.WriteLine(_skipReason);
            }

            return true;
        }

        return false;
    }
}
