using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Hugo;
using Hugo.DeterministicWorkerSample.Core;
using Hugo.Diagnostics.OpenTelemetry;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Diagnostics.CodeAnalysis;

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

// Share TimeProvider so the deterministic stores can agree on timestamps.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDeterministicStateStore, InMemoryDeterministicStateStore>();
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
builder.Services.AddSingleton<IPipelineEntityRepository, InMemoryPipelineEntityRepository>();
builder.Services.AddSingleton<PipelineSaga>();
builder.Services.AddSingleton<DeterministicPipelineProcessor>();
builder.Services.AddHostedService<KafkaWorker>();
builder.Services.AddHostedService<SampleScenario>();

IHost app = builder.Build();
await app.RunAsync().ConfigureAwait(false);
return;

/// <summary>
/// Configures serializer options used across deterministic stores in the sample.
/// </summary>
/// <returns>Serializer options that include sample-specific metadata.</returns>
[RequiresUnreferencedCode("Calls System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver.DefaultJsonTypeInfoResolver()")]
[RequiresDynamicCode("Calls System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver.DefaultJsonTypeInfoResolver()")]
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
