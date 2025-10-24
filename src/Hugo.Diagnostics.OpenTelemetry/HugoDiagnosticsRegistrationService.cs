using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hugo.Diagnostics.OpenTelemetry;

internal sealed class HugoDiagnosticsRegistrationService(
    IMeterFactory meterFactory,
    IOptions<HugoOpenTelemetryOptions> options) : IHostedService
{
    private readonly IMeterFactory _meterFactory = meterFactory ?? throw new ArgumentNullException(nameof(meterFactory));
    private readonly HugoOpenTelemetryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private ActivitySource? _activitySource;
    private IDisposable? _samplingSubscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.ConfigureMeter)
        {
            GoDiagnostics.Configure(_meterFactory, _options.MeterName);
        }

        if (_options.ConfigureActivitySource)
        {
            _activitySource = GoDiagnostics.CreateActivitySource(
                _options.ActivitySourceName,
                _options.ActivitySourceVersion,
                _options.SchemaUrl);

            GoDiagnostics.Configure(_activitySource);
        }

        if (_options.EnableRateLimitedSampling && _activitySource is not null)
        {
            _samplingSubscription = GoDiagnostics.UseRateLimitedSampling(
                _activitySource,
                Math.Max(1, _options.MaxActivitiesPerInterval),
                _options.SamplingInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : _options.SamplingInterval,
                _options.UnsampledActivityResult);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _samplingSubscription?.Dispose();
        _samplingSubscription = null;

        _activitySource?.Dispose();
        _activitySource = null;

        return Task.CompletedTask;
    }
}
