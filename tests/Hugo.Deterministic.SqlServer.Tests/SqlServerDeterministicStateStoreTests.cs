using System;

using Hugo;
using Hugo.Deterministic.SqlServer;

using Testcontainers.MsSql;

using Xunit;

internal sealed class SqlServerDeterministicStateStoreTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container;
    private bool _skip;
    private string? _skipReason;

    public SqlServerDeterministicStateStoreTests()
    {
        _container = new MsSqlBuilder()
            .WithPassword("yourStrong(!)Password")
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithCleanUp(true)
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _container.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _skip = true;
            _skipReason = $"SQL Server container unavailable: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public void SetAndGetRoundTripsRecord()
    {
        if (SkipIfNecessary())
        {
            return;
        }

        SqlServerDeterministicStateStoreOptions options = new()
        {
            ConnectionString = _container.GetConnectionString(),
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
        if (SkipIfNecessary())
        {
            return;
        }

        SqlServerDeterministicStateStoreOptions options = new()
        {
            ConnectionString = _container.GetConnectionString(),
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
