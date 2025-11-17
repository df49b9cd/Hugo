using System.Diagnostics.Metrics;

using Hugo;
using Hugo.Diagnostics.OpenTelemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Hugo.Tests.Diagnostics;

public class OpenTelemetryBuilderExtensionsTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask AddHugoDiagnostics_ShouldWireMetersAndOptions()
    {
        using var meterListener = new MeterListener();
        var measurements = new List<long>();

        meterListener.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Meter.Name == GoDiagnostics.MeterName && instrument.Name == "taskqueue.enqueued")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((_, measurement, _, _) => measurements.Add(measurement));
        meterListener.Start();

        var services = new ServiceCollection();

        services.AddSingleton<IMeterFactory, TestMeterFactory>();

        services
            .AddOpenTelemetry()
            .AddHugoDiagnostics(options =>
            {
                options.ServiceName = "otel-unit-tests";
                options.AddOtlpExporter = false;
                options.AddPrometheusExporter = false;
                options.AddRuntimeInstrumentation = false;
                options.ConfigureActivitySource = false;
                options.EnableRateLimitedSampling = false;
            })
            .WithMetrics(metrics => metrics.AddMeter(GoDiagnostics.MeterName));

        await using var provider = services.BuildServiceProvider();

        // Ensure Hugo diagnostics registration runs so instrumentation is active.
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(TestContext.Current.CancellationToken);
        }

        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "otel.queue", Capacity = 8 });
        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);

        SpinWait.SpinUntil(() => measurements.Count > 0, TimeSpan.FromSeconds(3)).ShouldBeTrue();
        measurements.ShouldNotBeEmpty();

        var options = provider.GetRequiredService<IOptions<HugoOpenTelemetryOptions>>();
        options.Value.ServiceName.ShouldBe("otel-unit-tests");

        GoDiagnostics.Reset();
    }

    private sealed class TestMeterFactory : IMeterFactory, IDisposable
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (Meter meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }
}
