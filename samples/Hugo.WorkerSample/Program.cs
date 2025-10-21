using Hugo;
using Hugo.Diagnostics.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Hugo.Go;

var builder = Host.CreateApplicationBuilder(args);

builder.AddHugoDiagnostics(options =>
{
    options.ServiceName = builder.Environment.ApplicationName;
    options.OtlpEndpoint = ResolveOtlpEndpoint(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    options.AddPrometheusExporter = ResolvePrometheusEnabled(builder.Configuration["HUGO_PROMETHEUS_ENABLED"]);
    options.MaxActivitiesPerInterval = 64;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TaskQueue<TelemetryWorkItem>>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var queueLogger = loggerFactory.CreateLogger("TelemetryQueue");
    var timeProvider = sp.GetRequiredService<TimeProvider>();

    var options = new TaskQueueOptions
    {
        Capacity = 128,
        LeaseDuration = TimeSpan.FromSeconds(8),
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        LeaseSweepInterval = TimeSpan.FromMilliseconds(500),
        RequeueDelay = TimeSpan.FromMilliseconds(750),
        MaxDeliveryAttempts = 3
    };

    return new TaskQueue<TelemetryWorkItem>(
        options,
        timeProvider,
        (context, cancellationToken) =>
        {
            queueLogger.LogWarning(
                "Dead-lettered {Kind} reading {Value} after {Attempts} attempts: {Error}",
                context.Value.Kind,
                context.Value.Value,
                context.Attempt,
                context.Error.Message);

            return ValueTask.CompletedTask;
        });
});
builder.Services.AddSingleton<IDeterministicStateStore, InMemoryDeterministicStateStore>();
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<IDeterministicStateStore>();
    var timeProvider = sp.GetRequiredService<TimeProvider>();
    return new VersionGate(store, timeProvider);
});
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<IDeterministicStateStore>();
    var timeProvider = sp.GetRequiredService<TimeProvider>();
    return new DeterministicEffectStore(store, timeProvider);
});
builder.Services.AddSingleton(sp =>
{
    var versionGate = sp.GetRequiredService<VersionGate>();
    var effectStore = sp.GetRequiredService<DeterministicEffectStore>();
    return new DeterministicGate(versionGate, effectStore);
});
builder.Services.AddSingleton<TelemetryCalibration>();
builder.Services.AddHostedService<TelemetryWorker>();

var app = builder.Build();
await app.RunAsync();

static Uri ResolveOtlpEndpoint(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
    ? endpoint
    : new Uri("http://localhost:4317");

static bool ResolvePrometheusEnabled(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    return value.Trim() switch
    {
        "0" => false,
        "false" or "False" or "FALSE" => false,
        _ => true
    };
}

sealed class TelemetryWorker(
    ILogger<TelemetryWorker> logger,
    TelemetryCalibration calibration,
    TimeProvider timeProvider,
    TaskQueue<TelemetryWorkItem> queue) : BackgroundService
{
    private readonly ILogger<TelemetryWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TelemetryCalibration _calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TaskQueue<TelemetryWorkItem> _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private CalibrationProfile? _calibrationProfile;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var calibrationResult = await _calibration.RunAsync(stoppingToken).ConfigureAwait(false);
        if (calibrationResult.IsFailure)
        {
            var error = calibrationResult.Error!;
            _logger.LogError(
                "Telemetry calibration failed ({Code}): {Message}",
                error.Code ?? ErrorCodes.Unspecified,
                error.Message);
            throw new InvalidOperationException("Telemetry calibration failed – see logs for details.");
        }

        _calibrationProfile = calibrationResult.Value;

        var wg = new WaitGroup();

        wg.Go(async ct =>
        {
            var heartbeat = 0;
            while (!ct.IsCancellationRequested)
            {
                heartbeat++;
                var heartbeatItem = new TelemetryWorkItem(TelemetryKind.Heartbeat, heartbeat, _timeProvider.GetUtcNow());
                await _queue.EnqueueAsync(heartbeatItem, ct).ConfigureAwait(false);
                await DelayAsync(TimeSpan.FromSeconds(1), _timeProvider, ct).ConfigureAwait(false);
            }
        }, stoppingToken);

        wg.Go(async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                var cpuReading = Random.Shared.NextDouble() * 150; // intentionally allow outliers
                var workItem = new TelemetryWorkItem(TelemetryKind.Cpu, cpuReading, _timeProvider.GetUtcNow());
                await _queue.EnqueueAsync(workItem, ct).ConfigureAwait(false);
                _logger.LogInformation("Queued cpu reading {Reading:F2}", cpuReading);
                await DelayAsync(TimeSpan.FromMilliseconds(350), _timeProvider, ct).ConfigureAwait(false);
            }
        }, stoppingToken);

        try
        {
            await ProcessQueueAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host is shutting down
        }

        try
        {
            await wg.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TaskQueueLease<TelemetryWorkItem> lease;

            try
            {
                lease = await _queue.LeaseAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            await ProcessLeaseAsync(lease, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ProcessLeaseAsync(TaskQueueLease<TelemetryWorkItem> lease, CancellationToken stoppingToken)
    {
        try
        {
            if (lease.Attempt > 1 && lease.LastError is not null)
            {
                _logger.LogWarning(
                    "Retrying {Kind} observed at {ObservedAt:o}: attempt {Attempt} after {Error}",
                    lease.Value.Kind,
                    lease.Value.ObservedAt,
                    lease.Attempt,
                    lease.LastError.Message);
            }

            switch (lease.Value.Kind)
            {
                case TelemetryKind.Heartbeat:
                    _logger.LogInformation("Processing heartbeat {Count}", lease.Value.Value);
                    await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
                    return;
                case TelemetryKind.Cpu:
                    await HandleCpuReadingAsync(lease, stoppingToken).ConfigureAwait(false);
                    return;
                default:
                    var unsupported = Error.From("Unsupported telemetry kind.", ErrorCodes.Validation)
                        .WithMetadata("kind", lease.Value.Kind.ToString());
                    await lease.FailAsync(unsupported, requeue: false, stoppingToken).ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            var canceled = Error.From("Processing canceled during shutdown.", ErrorCodes.TaskQueueAbandoned)
                .WithMetadata("kind", lease.Value.Kind.ToString())
                .WithMetadata("attempt", lease.Attempt);

            try
            {
                await lease.FailAsync(canceled, requeue: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // queue already disposed during shutdown
            }
        }
        catch (Exception ex)
        {
            var failure = Error.From("Telemetry processing encountered an unexpected error.", ErrorCodes.TaskQueueAbandoned)
                .WithMetadata("kind", lease.Value.Kind.ToString())
                .WithMetadata("attempt", lease.Attempt)
                .WithMetadata("exceptionType", ex.GetType().FullName);

            _logger.LogError(ex, "Unexpected failure processing {Kind}", lease.Value.Kind);
            await lease.FailAsync(failure, requeue: true, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleCpuReadingAsync(TaskQueueLease<TelemetryWorkItem> lease, CancellationToken stoppingToken)
    {
        var message = lease.Value;
        var profile = _calibrationProfile ?? throw new InvalidOperationException("Calibration profile has not been loaded.");

        // Simulate longer processing that requires periodic heartbeats to keep the lease alive.
        await DelayAsync(TimeSpan.FromSeconds(1.2), _timeProvider, stoppingToken).ConfigureAwait(false);
        await lease.HeartbeatAsync(stoppingToken).ConfigureAwait(false);
        await DelayAsync(TimeSpan.FromSeconds(0.8), _timeProvider, stoppingToken).ConfigureAwait(false);

        if (message.Value >= 0d && message.Value <= profile.MaxCpuPercent)
        {
            _logger.LogInformation(
                "CPU reading {Value:F2}%% captured at {ObservedAt:o} processed on attempt {Attempt} within calibrated max {Max:F2}",
                message.Value,
                message.ObservedAt,
                lease.Attempt,
                profile.MaxCpuPercent);

            if (message.Value >= profile.WarningCpuPercent)
            {
                _logger.LogWarning(
                    "Reading {Value:F2}%% exceeded warning threshold {Warning:F2}%% (calibration v{Version})",
                    message.Value,
                    profile.WarningCpuPercent,
                    profile.Version);
            }

            await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        var error = Error.From("CPU reading out of range.", ErrorCodes.Validation)
            .WithMetadata("observed", message.Value)
            .WithMetadata("attempt", lease.Attempt)
            .WithMetadata("observedAt", message.ObservedAt);

        var shouldRetry = lease.Attempt < 3;

        if (shouldRetry)
        {
            _logger.LogWarning(
                "Retries remaining for out-of-range CPU reading {Value:F2} (attempt {Attempt})", message.Value, lease.Attempt);
        }
        else
        {
            _logger.LogWarning(
                "Marking out-of-range CPU reading {Value:F2} as dead-letter candidate (attempt {Attempt})",
                message.Value,
                lease.Attempt);
        }

        await lease.FailAsync(error, shouldRetry, stoppingToken).ConfigureAwait(false);
    }
}

enum TelemetryKind
{
    Heartbeat,
    Cpu
}

readonly record struct TelemetryWorkItem(TelemetryKind Kind, double Value, DateTimeOffset ObservedAt);

sealed class TelemetryCalibration(
    DeterministicGate gate,
    TimeProvider timeProvider,
    ILogger<TelemetryCalibration> logger)
{
    private readonly DeterministicGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<TelemetryCalibration> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<Result<CalibrationProfile>> RunAsync(CancellationToken cancellationToken)
    {
        var result = await _gate.Workflow<CalibrationProfile>("telemetry.calibration", 1, 2, _ => 2)
            .ForVersion(1, (ctx, ct) => ctx.CaptureAsync(
                "legacy-profile",
                _ => Task.FromResult(Result.Ok(new CalibrationProfile(100, 90, ctx.Version, _timeProvider.GetUtcNow()))),
                ct))
            .ForVersion(2, (ctx, ct) => ctx.CaptureAsync(
                "profile.v2",
                async token =>
                {
                    if (ctx.IsNew)
                    {
                        _logger.LogInformation("Running calibration change {ChangeId} version {Version}", ctx.ChangeId, ctx.Version);
                    }

                    await DelayAsync(TimeSpan.FromSeconds(1.5), _timeProvider, token).ConfigureAwait(false);

                    var profile = new CalibrationProfile(97.5, 92.5, ctx.Version, _timeProvider.GetUtcNow());
                    return Result.Ok(profile);
                },
                ct))
            .WithFallback((ctx, ct) => ctx.CaptureAsync(
                "fallback-profile",
                _ => Task.FromResult(Result.Ok(new CalibrationProfile(100, 95, ctx.Version, _timeProvider.GetUtcNow()))),
                ct))
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return result.Tap(profile =>
        {
            _logger.LogInformation(
                "Calibrated telemetry thresholds: max {Max:F2}%% warning {Warning:F2}%% (version {Version})",
                profile.MaxCpuPercent,
                profile.WarningCpuPercent,
                profile.Version);
        });
    }
}

readonly record struct CalibrationProfile(double MaxCpuPercent, double WarningCpuPercent, int Version, DateTimeOffset CapturedAt);
