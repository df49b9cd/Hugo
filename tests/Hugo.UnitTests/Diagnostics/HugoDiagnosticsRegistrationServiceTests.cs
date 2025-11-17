using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

using Hugo.Diagnostics.OpenTelemetry;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Hugo.Tests.Diagnostics;

public class HugoDiagnosticsRegistrationServiceTests
{
    [Fact(Timeout = 15_000)]
    public async Task StartAsync_ShouldConfigureMeterAndActivitySource_WhenEnabled()
    {
        GoDiagnostics.Reset();

        var factory = new RecordingMeterFactory();
        var options = new HugoOpenTelemetryOptions
        {
            ConfigureMeter = true,
            ConfigureActivitySource = true,
            EnableRateLimitedSampling = true,
            MaxActivitiesPerInterval = 2,
            SamplingInterval = TimeSpan.FromMilliseconds(5),
            ActivitySourceName = "tests.source",
            ActivitySourceVersion = "1.2.3"
        };

        IHostedService service = CreateService(factory, options);

        await service.StartAsync(TestContext.Current.CancellationToken);

        factory.CreatedMeterNames.ShouldContain(GoDiagnostics.MeterName);

        var activitySource = GetPrivate<ActivitySource>(service, "_activitySource");
        activitySource.ShouldNotBeNull();
        activitySource!.Name.ShouldBe("tests.source");

        await service.StopAsync(TestContext.Current.CancellationToken);

        GoDiagnostics.Reset();
    }

    [Fact(Timeout = 15_000)]
    public async Task StartAsync_ShouldSkipMeterAndSamplingWhenDisabled()
    {
        GoDiagnostics.Reset();

        var factory = new RecordingMeterFactory();
        var options = new HugoOpenTelemetryOptions
        {
            ConfigureMeter = false,
            ConfigureActivitySource = false,
            EnableRateLimitedSampling = true
        };

        IHostedService service = CreateService(factory, options);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        factory.CreatedMeterNames.ShouldBeEmpty();
        GetPrivate<ActivitySource>(service, "_activitySource").ShouldBeNull();

        GoDiagnostics.Reset();
    }

    private static IHostedService CreateService(IMeterFactory factory, HugoOpenTelemetryOptions options)
    {
        Type type = Type.GetType("Hugo.Diagnostics.OpenTelemetry.HugoDiagnosticsRegistrationService, Hugo.Diagnostics.OpenTelemetry", throwOnError: true)!;
        return (IHostedService)Activator.CreateInstance(type, factory, Options.Create(options))!;
    }

    private static T? GetPrivate<T>(object instance, string fieldName)
    {
        FieldInfo? field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return (T?)field?.GetValue(instance);
    }

    private sealed class RecordingMeterFactory : IMeterFactory, IAsyncDisposable, IDisposable
    {
        private readonly List<Meter> _meters = [];

        internal List<string> CreatedMeterNames { get; } = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            CreatedMeterNames.Add(meter.Name);
            _meters.Add(meter);
            return meter;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
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
