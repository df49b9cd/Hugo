using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Linq;

using Hugo;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Program entry point wires together the worker pipeline:
//  SampleScenario publishes scripted messages -> SimulatedKafkaTopic ->
//  KafkaWorker consumes them -> DeterministicPipelineProcessor executes the saga ->
//  PipelineEntityStore persists deterministic results for replay safety.
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

// Share TimeProvider so the deterministic stores can agree on timestamps.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDeterministicStateStore, InMemoryDeterministicStateStore>();
builder.Services.AddSingleton(sp =>
{
    // Reuse the same serializer options for version markers so replay metadata stays consistent.
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    JsonSerializerOptions options = CreateSampleSerializerOptions();
    return new VersionGate(store, timeProvider, options);
});
builder.Services.AddSingleton(sp =>
{
    // The deterministic effect store shares the serializer options to persist saga outcomes.
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    JsonSerializerOptions options = CreateSampleSerializerOptions();
    return new DeterministicEffectStore(store, timeProvider, options);
});
builder.Services.AddSingleton<DeterministicGate>();
builder.Services.AddSingleton<SimulatedKafkaTopic>();
builder.Services.AddSingleton<PipelineEntityStore>();
builder.Services.AddSingleton<PipelineSaga>();
builder.Services.AddSingleton<DeterministicPipelineProcessor>();
builder.Services.AddHostedService<KafkaWorker>();
builder.Services.AddHostedService<SampleScenario>();

IHost app = builder.Build();
await app.RunAsync();
return;

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
