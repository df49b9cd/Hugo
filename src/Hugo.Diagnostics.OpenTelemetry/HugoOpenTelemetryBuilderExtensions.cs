using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.Runtime;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Hugo.Diagnostics.OpenTelemetry;

/// <summary>
/// Extension helpers for wiring <see cref="GoDiagnostics"/> into OpenTelemetry pipelines.
/// </summary>
public static class HugoOpenTelemetryBuilderExtensions
{
    /// <summary>
    /// Adds Hugo diagnostics exporters and instrumentation to the supplied OpenTelemetry builder.
    /// </summary>
    /// <param name="builder">The OpenTelemetry builder.</param>
    /// <param name="configure">Optional configuration delegate.</param>
    /// <returns>The provided builder.</returns>
    public static OpenTelemetryBuilder AddHugoDiagnostics(this OpenTelemetryBuilder builder, Action<HugoOpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new HugoOpenTelemetryOptions();
        configure?.Invoke(options);

        return builder.AddHugoDiagnostics(options);
    }

    /// <summary>
    /// Adds Hugo diagnostics exporters and instrumentation to the supplied OpenTelemetry builder.
    /// </summary>
    /// <param name="builder">The OpenTelemetry builder.</param>
    /// <param name="options">Pre-configured options instance.</param>
    /// <returns>The provided builder.</returns>
    public static OpenTelemetryBuilder AddHugoDiagnostics(this OpenTelemetryBuilder builder, HugoOpenTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HugoDiagnosticsRegistrationService>());
        builder.Services.TryAddSingleton<IOptions<HugoOpenTelemetryOptions>>(_ => Options.Create(options));

        builder.ConfigureResource(resourceBuilder =>
        {
            if (!string.IsNullOrWhiteSpace(options.ServiceName))
            {
                resourceBuilder.AddService(options.ServiceName);
            }

            if (options.AttachSchemaAttribute && !string.IsNullOrWhiteSpace(options.SchemaUrl))
            {
                    resourceBuilder.AddAttributes(new[]
                    {
                        new KeyValuePair<string, object>("telemetry.schema.url", options.SchemaUrl!)
                    });
            }
        });

        builder.WithMetrics(metrics =>
        {
            metrics.AddMeter(options.MeterName);

            if (options.AddRuntimeInstrumentation)
            {
                metrics.AddRuntimeInstrumentation();
            }

            if (options.AddPrometheusExporter)
            {
                metrics.AddPrometheusExporter();
            }

            if (options.AddOtlpExporter)
            {
                metrics.AddOtlpExporter(exporterOptions =>
                {
                    if (options.OtlpEndpoint is { } endpoint)
                    {
                        exporterOptions.Endpoint = endpoint;
                    }

                    exporterOptions.Protocol = options.OtlpProtocol;
                });
            }
        });

        builder.WithTracing(tracing =>
        {
            tracing.AddSource(options.ActivitySourceName);

            if (options.AddOtlpExporter)
            {
                tracing.AddOtlpExporter(exporterOptions =>
                {
                    if (options.OtlpEndpoint is { } endpoint)
                    {
                        exporterOptions.Endpoint = endpoint;
                    }

                    exporterOptions.Protocol = options.OtlpProtocol;
                });
            }
        });

        return builder;
    }

    /// <summary>
    /// Adds Hugo diagnostics defaults to the provided host builder, mirroring Aspire ServiceDefaults conventions.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">Optional configuration delegate.</param>
    /// <returns>The provided builder.</returns>
    public static IHostApplicationBuilder AddHugoDiagnostics(this IHostApplicationBuilder builder, Action<HugoOpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new HugoOpenTelemetryOptions
        {
            ServiceName = builder.Environment.ApplicationName
        };

        configure?.Invoke(options);

        builder.Services.AddOpenTelemetry().AddHugoDiagnostics(options);
        return builder;
    }
}
