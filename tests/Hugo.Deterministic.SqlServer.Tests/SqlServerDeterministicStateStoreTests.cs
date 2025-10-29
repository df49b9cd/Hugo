using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Configurations;

using Hugo;
using Hugo.Deterministic.SqlServer;

using Xunit;

public sealed class SqlServerDeterministicStateStoreTests : IAsyncLifetime
{
    private readonly MsSqlTestcontainer _container;

    public SqlServerDeterministicStateStoreTests()
    {
        MsSqlTestcontainerConfiguration configuration = new()
        {
            Password = "yourStrong(!)Password"
        };

        _container = new TestcontainersBuilder<MsSqlTestcontainer>()
            .WithDatabase(configuration)
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithCleanUp(true)
            .Build();
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    [Fact]
    public void SetAndGetRoundTripsRecord()
    {
        SqlServerDeterministicStateStoreOptions options = new()
        {
            ConnectionString = _container.ConnectionString,
            AutoCreateTable = true,
            Schema = "dbo",
            TableName = "DeterministicRecords"
        };

        SqlServerDeterministicStateStore store = new(options);

        byte[] payload = [1, 2, 3, 4];
        DeterministicRecord record = new("workflow.test", 1, payload, DateTimeOffset.UtcNow);

        store.Set("roundtrip", record);

        Assert.True(store.TryGet("roundtrip", out DeterministicRecord stored));
        Assert.Equal(record.Kind, stored.Kind);
        Assert.Equal(record.Version, stored.Version);
        Assert.Equal(record.RecordedAt, stored.RecordedAt, TimeSpan.FromSeconds(1));
        Assert.Equal(record.Payload.ToArray(), stored.Payload.ToArray());
    }

    [Fact]
    public void SetOverwritesExistingRecord()
    {
        SqlServerDeterministicStateStoreOptions options = new()
        {
            ConnectionString = _container.ConnectionString,
            AutoCreateTable = true,
            Schema = "dbo",
            TableName = "DeterministicRecords"
        };

        SqlServerDeterministicStateStore store = new(options);

        DeterministicRecord first = new("workflow.test", 1, [1, 1, 1], DateTimeOffset.UtcNow);
        DeterministicRecord second = new("workflow.test", 2, [2, 2, 2], DateTimeOffset.UtcNow.AddMinutes(1));

        store.Set("update", first);
        store.Set("update", second);

        Assert.True(store.TryGet("update", out DeterministicRecord stored));
        Assert.Equal(second.Version, stored.Version);
        Assert.Equal(second.Payload.ToArray(), stored.Payload.ToArray());
        Assert.Equal(second.RecordedAt, stored.RecordedAt, TimeSpan.FromSeconds(1));
    }
}
