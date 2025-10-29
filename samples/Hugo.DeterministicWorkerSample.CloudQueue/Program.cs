using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Azure.Storage.Queues;

using Hugo;
using Hugo.Diagnostics.OpenTelemetry;
using Hugo.Deterministic.Cosmos;
using Hugo.DeterministicWorkerSample.CloudQueue;
using Hugo.DeterministicWorkerSample.Core;

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Program entry point wires together the worker pipeline:
//  SampleScenario publishes scripted messages -> SimulatedKafkaTopic ->
//  KafkaWorker consumes them -> DeterministicPipelineProcessor executes the saga ->
//  IPipelineEntityRepository persists deterministic results for replay safety.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLogging(logging =>
{
    logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = false;
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});

// Telemetry defaults to console exporters; toggle exporters through configuration or environment variables.
bool consoleExporterEnabled = builder.Configuration.GetValue("Telemetry:ConsoleExporterEnabled", true);
bool otlpExporterEnabled = builder.Configuration.GetValue("Telemetry:OtlpExporterEnabled", false);
bool prometheusExporterEnabled = builder.Configuration.GetValue("Telemetry:PrometheusExporterEnabled", builder.Configuration.GetValue("HUGO_PROMETHEUS_ENABLED", false));

Uri? otlpEndpoint = null;
string? otlpEndpointValue = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpointValue) && Uri.TryCreate(otlpEndpointValue, UriKind.Absolute, out Uri? parsedEndpoint))
{
    otlpEndpoint = parsedEndpoint;
    otlpExporterEnabled = true;
}

builder.Services.AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = builder.Environment.ApplicationName;
        options.AddPrometheusExporter = prometheusExporterEnabled;
        options.AddOtlpExporter = otlpExporterEnabled;
        options.AddRuntimeInstrumentation = false;
        if (otlpEndpoint is not null)
        {
            options.OtlpEndpoint = otlpEndpoint;
        }
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource(DeterministicPipelineTelemetry.ActivitySourceName);
        if (consoleExporterEnabled)
        {
            tracing.AddConsoleExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(DeterministicPipelineTelemetry.MeterName);
        if (consoleExporterEnabled)
        {
            metrics.AddConsoleExporter();
        }
    });

string queueConnectionString = ResolveQueueConnectionString(builder.Configuration);
string queueName = builder.Configuration.GetValue("Queue:Name", "deterministic-events");
builder.Services.AddSingleton(new QueueClient(queueConnectionString, queueName));

CosmosClient cosmosClient = new(ResolveCosmosConnectionString(builder.Configuration), new CosmosClientOptions
{
    ApplicationName = builder.Environment.ApplicationName
});
builder.Services.AddSingleton(cosmosClient);

builder.Services.AddHugoDeterministicCosmos(options =>
{
    options.Client = cosmosClient;
    options.DatabaseId = builder.Configuration.GetValue("Cosmos:Database", "hugo-deterministic");
    options.ContainerId = builder.Configuration.GetValue("Cosmos:DeterministicContainer", "deterministic-effects");
    options.PartitionKeyPath = "/id";
});

builder.Services.AddOptions<CosmosPipelineOptions>().Configure(options =>
{
    options.DatabaseId = builder.Configuration.GetValue("Cosmos:Database", "hugo-deterministic");
    options.ContainerId = builder.Configuration.GetValue("Cosmos:PipelineContainer", "pipeline-projections");
    options.PartitionKeyPath = builder.Configuration.GetValue("Cosmos:PipelinePartitionKey", "/id");
    options.CreateResources = builder.Configuration.GetValue("Cosmos:CreateResources", true);
});
builder.Services.AddSingleton<CosmosPipelineEntityRepository>();

// Share TimeProvider so deterministic components agree on timestamps.
builder.Services.AddSingleton(TimeProvider.System);
JsonSerializerOptions serializerOptions = CreateSampleSerializerOptions();
builder.Services.AddSingleton(sp =>
{
    // Reuse the same serializer options for version markers so replay metadata stays consistent.
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    return new VersionGate(store, timeProvider, serializerOptions);
});
builder.Services.AddSingleton(sp =>
{
    // The deterministic effect store shares the serializer options to persist saga outcomes.
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    return new DeterministicEffectStore(store, timeProvider, serializerOptions);
});
builder.Services.AddSingleton<DeterministicGate>();
builder.Services.AddSingleton<IPipelineEntityRepository>(sp => sp.GetRequiredService<CosmosPipelineEntityRepository>());
builder.Services.AddSingleton<PipelineSaga>();
builder.Services.AddSingleton<DeterministicPipelineProcessor>();
builder.Services.AddHostedService<QueueWorker>();
builder.Services.AddHostedService<QueuePublisher>();

IHost app = builder.Build();
await app.RunAsync().ConfigureAwait(false);
return;

static string ResolveQueueConnectionString(IConfiguration configuration)
{
    string? connectionString = configuration["Queue:ConnectionString"] ?? configuration.GetConnectionString("Queue") ?? Environment.GetEnvironmentVariable("HUGO_SAMPLE_QUEUE");
    return string.IsNullOrWhiteSpace(connectionString) ? "UseDevelopmentStorage=true" : connectionString;
}

static string ResolveCosmosConnectionString(IConfiguration configuration)
{
    string? connectionString = configuration["Cosmos:ConnectionString"] ?? Environment.GetEnvironmentVariable("HUGO_SAMPLE_COSMOS");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        // Emulator connection string.
        connectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4DG8q0wE=";
    }

    return connectionString;
}

/// <summary>
/// Configures serializer options used across deterministic stores in the sample.
/// </summary>
/// <returns>Serializer options that include sample-specific metadata.</returns>
static JsonSerializerOptions CreateSampleSerializerOptions()
{
    JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    if (!options.TypeInfoResolverChain.Contains(DeterministicPipelineSerializerContext.Default))
    {
        // First resolver handles sample-specific models captured by the effect store.
        options.TypeInfoResolverChain.Insert(0, DeterministicPipelineSerializerContext.Default);
    }

    if (!options.TypeInfoResolverChain.OfType<DefaultJsonTypeInfoResolver>().Any())
    {
        // Fallback resolver keeps built-in types like VersionGate markers functional.
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
    }

    return options;
}
