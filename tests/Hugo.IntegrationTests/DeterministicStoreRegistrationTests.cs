using Hugo.Deterministic.Cosmos;
using Hugo.Deterministic.Redis;
using Hugo.Deterministic.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hugo.Tests;

public sealed class DeterministicStoreRegistrationTests
{
    [Fact(Timeout = 15_000)]
    public void AddHugoDeterministicCosmos_ShouldRegisterOptionsAndStore()
    {
        ServiceCollection services = new();

        services.AddHugoDeterministicCosmos(options =>
        {
            options.DatabaseId = "db";
            options.ContainerId = "container";
        });

        ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<CosmosDeterministicStateStoreOptions>();

        options.Client.ShouldBeNull();
        options.DatabaseId.ShouldBe("db");
        options.ContainerId.ShouldBe("container");
    }

    [Fact(Timeout = 15_000)]
    public void AddHugoDeterministicCosmos_ShouldThrowWhenResolvingWithoutClient()
    {
        ServiceCollection services = new();
        services.AddHugoDeterministicCosmos(options =>
        {
            options.DatabaseId = "db";
            options.ContainerId = "container";
        });

        ServiceProvider provider = services.BuildServiceProvider();

        Should.Throw<ArgumentException>(() => provider.GetRequiredService<IDeterministicStateStore>());
    }

    [Fact(Timeout = 15_000)]
    public void AddHugoDeterministicRedis_ShouldRegisterOptions()
    {
        ServiceCollection services = new();

        services.AddHugoDeterministicRedis(options =>
        {
            options.Database = 5;
        });

        ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<RedisDeterministicStateStoreOptions>();

        options.Database.ShouldBe(5);
        options.ConnectionMultiplexer.ShouldBeNull();
    }

    [Fact(Timeout = 15_000)]
    public void AddHugoDeterministicSqlServer_ShouldRegisterOptions()
    {
        ServiceCollection services = new();

        const string connectionString = "Server=.;Database=Hugo;User Id=sa;Password=Pass@word1;";

        services.AddHugoDeterministicSqlServer(connectionString, options =>
        {
            options.Schema = "custom";
            options.TableName = "Deterministic";
            options.AutoCreateTable = false;
        });

        ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<SqlServerDeterministicStateStoreOptions>();

        options.ConnectionString.ShouldBe(connectionString);
        options.Schema.ShouldBe("custom");
        options.TableName.ShouldBe("Deterministic");
        options.AutoCreateTable.ShouldBeFalse();
    }
}
