using System;
using System.Net.Http;
using System.Threading;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

using Hugo;
using Hugo.Deterministic.Cosmos;

using Microsoft.Azure.Cosmos;

using Testcontainers.CosmosDb;

using Xunit;

public sealed class CosmosDeterministicStateStoreTests : IAsyncLifetime
{
    private const string PrimaryKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4DG8q0wE=";
    private const string DatabaseId = "hugo-deterministic-tests";
    private const string ContainerId = "deterministic-effects";

    private readonly CosmosDbContainer _container = new CosmosDbBuilder()
        .WithImage("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest")
        .WithCleanUp(true)
        .WithPrivileged(true)
        .WithPortBinding(8081, true)
        .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "3")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_TELEMETRY", "false")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_MONGODB_ENDPOINT", "false")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_CASSANDRA_ENDPOINT", "false")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_TABLE_ENDPOINT", "false")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_GREMLIN_ENDPOINT", "false")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "0.0.0.0")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_CERTIFICATE_PASSWORD", "CosmosEmulator")
        .WithEnvironment("AZURE_COSMOS_EMULATOR_ACCEPT_EULA", "Y")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8081))
        .Build();

    private CosmosClient? _client;
    private Uri? _endpoint;
    private bool _skip;
    private string? _skipReason;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _container.StartAsync().ConfigureAwait(false);

            int mappedPort = _container.GetMappedPublicPort(8081);
            _endpoint = new Uri($"https://localhost:{mappedPort}/");

            await WaitForEmulatorAsync(_endpoint, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

            CosmosClientOptions clientOptions = new()
            {
                HttpClientFactory = CreateInsecureHttpClient,
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
            };

            _client = new CosmosClient(_endpoint.ToString(), PrimaryKey, clientOptions);
            await _client.CreateDatabaseIfNotExistsAsync(DatabaseId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _skip = true;
            _skipReason = $"Cosmos emulator unavailable: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    [Fact(Timeout = 15_000)]
    public void SetAndGetRoundTripsRecord()
    {
        if (SkipIfNecessary())
        {
            return;
        }

        CosmosDeterministicStateStore store = CreateStore();

        DeterministicRecord record = new("workflow.test", 1, [1, 2, 3], DateTimeOffset.UtcNow);
        store.Set("roundtrip", record);

        Assert.True(store.TryGet("roundtrip", out DeterministicRecord stored));
        Assert.Equal(record.Kind, stored.Kind);
        Assert.Equal(record.Version, stored.Version);
        Assert.Equal(record.Payload.ToArray(), stored.Payload.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public void SetOverwritesExistingRecord()
    {
        if (SkipIfNecessary())
        {
            return;
        }

        CosmosDeterministicStateStore store = CreateStore();

        DeterministicRecord first = new("workflow.test", 1, [1], DateTimeOffset.UtcNow);
        DeterministicRecord second = new("workflow.test", 2, [2], DateTimeOffset.UtcNow.AddMinutes(1));

        store.Set("update", first);
        store.Set("update", second);

        Assert.True(store.TryGet("update", out DeterministicRecord stored));
        Assert.Equal(second.Version, stored.Version);
        Assert.Equal(second.Payload.ToArray(), stored.Payload.ToArray());
    }

    private CosmosDeterministicStateStore CreateStore()
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Cosmos client is not initialised.");
        }

        CosmosDeterministicStateStoreOptions options = new()
        {
            Client = _client,
            DatabaseId = DatabaseId,
            ContainerId = ContainerId,
            PartitionKeyPath = "/id",
            CreateResources = true
        };

        return new CosmosDeterministicStateStore(options);
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

    private static async Task WaitForEmulatorAsync(Uri endpoint, TimeSpan timeout)
    {
        using HttpClient client = CreateInsecureHttpClient();
        Uri healthUri = new(endpoint, "_explorer/emulator.pem");

        using CancellationTokenSource cts = new(timeout);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(healthUri, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Emulator not ready yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for Cosmos emulator to become available.");
    }

    private static HttpClient CreateInsecureHttpClient()
    {
        HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler, disposeHandler: true);
    }
}
