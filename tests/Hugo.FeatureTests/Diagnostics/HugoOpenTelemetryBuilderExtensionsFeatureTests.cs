using System.Linq;

using Hugo.Diagnostics.OpenTelemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Hugo.Tests;

public sealed class HugoOpenTelemetryBuilderExtensionsFeatureTests
{
    [Fact(Timeout = 15_000)]
    public void AddHugoDiagnostics_ShouldRegisterOptionsAndHostedService()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });

        builder.AddHugoDiagnostics(options =>
        {
            options.ServiceName = "feature-service";
            options.AddOtlpExporter = true;
            options.AddPrometheusExporter = true;
            options.AttachSchemaAttribute = true;
            options.SchemaUrl = "https://schema.test";
            options.AddRuntimeInstrumentation = true;
        });

        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<HugoOpenTelemetryOptions>>().Value;
        options.ServiceName.ShouldBe("feature-service");
        options.AddOtlpExporter.ShouldBeTrue();
        options.AddPrometheusExporter.ShouldBeTrue();
        options.AttachSchemaAttribute.ShouldBeTrue();
        options.SchemaUrl.ShouldBe("https://schema.test");
        options.AddRuntimeInstrumentation.ShouldBeTrue();

        host.Services.GetServices<IHostedService>()
            .Any(service => service.GetType().Name == "HugoDiagnosticsRegistrationService")
            .ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public void AddHugoDiagnostics_WithOptionsInstance_ShouldRegisterSingletonInstance()
    {
        var builder = Host.CreateApplicationBuilder();
        var options = new HugoOpenTelemetryOptions
        {
            ServiceName = "options-instance"
        };

        builder.Services.AddOpenTelemetry().AddHugoDiagnostics(options);

        using var host = builder.Build();

        var resolved = host.Services.GetRequiredService<IOptions<HugoOpenTelemetryOptions>>().Value;
        ReferenceEquals(resolved, options).ShouldBeTrue();

        host.Services.GetServices<IHostedService>()
            .Any(service => service.GetType().Name == "HugoDiagnosticsRegistrationService")
            .ShouldBeTrue();
    }
}
