using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Hugo;
using Hugo.Diagnostics.OpenTelemetry;
using Hugo.Deterministic.SqlServer;
using Hugo.DeterministicWorkerSample.Core;
using Hugo.DeterministicWorkerSample.Relational;

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

string sqlConnectionString = ResolveConnectionString(builder.Configuration);
builder.Services.AddHugoDeterministicSqlServer(sqlConnectionString, options =>
{
    options.Schema = builder.Configuration.GetValue("Deterministic:SqlServer:Schema", "dbo");
    options.TableName = builder.Configuration.GetValue("Deterministic:SqlServer:TableName", "DeterministicRecords");
});

builder.Services.AddOptions<SqlServerPipelineOptions>().Configure(options =>
{
    options.ConnectionString = sqlConnectionString;
    options.Schema = builder.Configuration.GetValue("Pipeline:SqlServer:Schema", "dbo");
    options.TableName = builder.Configuration.GetValue("Pipeline:SqlServer:TableName", "PipelineEntities");
});
builder.Services.AddSingleton<SqlServerPipelineSchemaMigrator>();

// Share TimeProvider so deterministic stores can agree on timestamps.
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
builder.Services.AddSingleton<SimulatedKafkaTopic>();
builder.Services.AddSingleton<IPipelineEntityRepository, SqlServerPipelineEntityRepository>();
builder.Services.AddSingleton<PipelineSaga>();
builder.Services.AddSingleton<DeterministicPipelineProcessor>();
builder.Services.AddHostedService<KafkaWorker>();
builder.Services.AddHostedService<SampleScenario>();

IHost app = builder.Build();
await app.RunAsync().ConfigureAwait(false);
return;

static string ResolveConnectionString(IConfiguration configuration)
{
    string? pipelineSection = configuration["Pipeline:SqlServer:ConnectionString"];
    string? deterministicSection = configuration["Deterministic:SqlServer:ConnectionString"];
    string? connectionString = pipelineSection;

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        connectionString = deterministicSection;
    }

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        connectionString = configuration.GetConnectionString("Pipeline")
            ?? configuration.GetConnectionString("Deterministic")
            ?? Environment.GetEnvironmentVariable("HUGO_SAMPLE_SQLSERVER");
    }

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        // Default to a development connection string targeting a local SQL Server instance.
        connectionString = "Server=localhost,1433;Database=HugoDeterministicSample;User ID=sa;Password=Your_password123;TrustServerCertificate=True";
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
