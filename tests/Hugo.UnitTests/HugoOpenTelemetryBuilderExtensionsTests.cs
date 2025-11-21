using Hugo.Diagnostics.OpenTelemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenTelemetry.Exporter;


namespace Hugo.Tests;

public sealed class HugoOpenTelemetryBuilderExtensionsTests
{
    [Fact(Timeout = 5_000)]
    public void AddHugoDiagnostics_ShouldConfigureOtlpExporter()
    {
        var services = new ServiceCollection();
        var builder = services.AddOpenTelemetry();

        builder.AddHugoDiagnostics(options =>
        {
            options.AddPrometheusExporter = false;
            options.AddRuntimeInstrumentation = false;
            options.OtlpEndpoint = new Uri("http://localhost:4318");
            options.OtlpProtocol = OtlpExportProtocol.HttpProtobuf;
        });

        using var provider = services.BuildServiceProvider();
        var exporterOptions = provider.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().CurrentValue;

        exporterOptions.ShouldNotBeNull();
        exporterOptions.Endpoint.ShouldNotBeNull();
    }
}
