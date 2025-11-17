using Hugo.Diagnostics.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using Shouldly;

namespace Hugo.Tests.Diagnostics;

public sealed class HugoOpenTelemetryBuilderExtensionsTests
{
    [Fact(Timeout = 15_000)]
    public void AddHugoDiagnostics_ShouldRegisterHostedServiceAndOptions()
    {
        var hostBuilder = Host.CreateApplicationBuilder();

        hostBuilder.AddHugoDiagnostics(options =>
        {
            options.ServiceName = "otel-test";
            options.AttachSchemaAttribute = true;
            options.SchemaUrl = "http://schema";
            options.AddOtlpExporter = true;
            options.OtlpEndpoint = new Uri("http://localhost:4317");
            options.AddRuntimeInstrumentation = true;
            options.AddPrometheusExporter = true;
        });

        try
        {
            using var provider = hostBuilder.Services.BuildServiceProvider();

            provider.GetServices<IHostedService>()
                .ShouldContain(service => service.GetType().FullName == "Hugo.Diagnostics.OpenTelemetry.HugoDiagnosticsRegistrationService");
            var options = provider.GetRequiredService<IOptions<HugoOpenTelemetryOptions>>().Value;
            options.ServiceName.ShouldBe("otel-test");
            options.SchemaUrl.ShouldBe("http://schema");
            options.AddOtlpExporter.ShouldBeTrue();
        }
        finally
        {
            GoDiagnostics.Reset();
        }
    }

    [Fact(Timeout = 15_000)]
    public void AddHugoDiagnostics_OpenTelemetryBuilderOverload_ShouldHonorDefaults()
    {
        var services = new ServiceCollection();
        var builder = services.AddOpenTelemetry();

        try
        {
            builder.AddHugoDiagnostics();

            services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                       && string.Equals(descriptor.ImplementationType?.FullName, "Hugo.Diagnostics.OpenTelemetry.HugoDiagnosticsRegistrationService", StringComparison.Ordinal))
                .ShouldBeTrue();
        }
        finally
        {
            GoDiagnostics.Reset();
        }
    }
}
