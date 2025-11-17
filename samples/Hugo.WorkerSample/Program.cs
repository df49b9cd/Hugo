using System.Threading.Channels;

using Hugo;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using static Hugo.Go;

var builder = WebApplication.CreateBuilder(args);

// builder.Services
//     .AddOpenTelemetry()
//     .AddHugoDiagnostics(options =>
//     {
//         options.ServiceName = builder.Environment.ApplicationName;
//         options.OtlpEndpoint = ResolveOtlpEndpoint(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
//         options.AddPrometheusExporter = ResolvePrometheusEnabled(builder.Configuration["HUGO_PROMETHEUS_ENABLED"]);
//         options.MaxActivitiesPerInterval = 64;
//     })
//     .WithTracing(tracing =>
//     {
//         tracing.AddConsoleExporter();
//     })
//     .WithMetrics(metrics =>
//     {
//         metrics.AddConsoleExporter();
//     });

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<TelemetryQueueOptions>(builder.Configuration.GetSection("TelemetryQueue"));
builder.Services.AddSingleton(sp =>
{
    ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    ILogger queueLogger = loggerFactory.CreateLogger("TelemetryQueue");
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    TelemetryQueueOptions telemetryOptions = sp.GetRequiredService<IOptions<TelemetryQueueOptions>>().Value;

    TaskQueueOptions options = new()
    {
        Capacity = 48,
        LeaseDuration = TimeSpan.FromMilliseconds(telemetryOptions.LeaseDurationMs),
        HeartbeatInterval = TimeSpan.FromMilliseconds(telemetryOptions.HeartbeatIntervalMs),
        LeaseSweepInterval = TimeSpan.FromMilliseconds(telemetryOptions.LeaseSweepIntervalMs),
        RequeueDelay = TimeSpan.FromMilliseconds(telemetryOptions.RequeueDelayMs),
        MaxDeliveryAttempts = telemetryOptions.MaxDeliveryAttempts
    };

    return new TaskQueue<TelemetryWorkItem>(
        options,
        timeProvider,
        (context, _) =>
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
builder.Services.AddSingleton(sp => new SafeTaskQueueWrapper<TelemetryWorkItem>(sp.GetRequiredService<TaskQueue<TelemetryWorkItem>>()));
builder.Services.AddSingleton<IDeterministicStateStore, InMemoryDeterministicStateStore>();
builder.Services.AddTaskQueueHealthCheck<TelemetryWorkItem>(
    "telemetry-queue",
    sp => sp.GetRequiredService<TaskQueue<TelemetryWorkItem>>(),
    options =>
    {
        options.PendingDegradedThreshold = 24;
        options.PendingUnhealthyThreshold = 48;
        options.ActiveLeaseDegradedThreshold = 4;
        options.ActiveLeaseUnhealthyThreshold = 8;
    });
builder.Services.AddSingleton(sp =>
{
    SafeTaskQueueWrapper<TelemetryWorkItem> safeQueue = sp.GetRequiredService<SafeTaskQueueWrapper<TelemetryWorkItem>>();
    return TaskQueueChannelAdapter<TelemetryWorkItem>.Create(safeQueue.UnsafeQueue, concurrency: 1);
});
builder.Services.AddSingleton(sp =>
{
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    return new VersionGate(store, timeProvider);
});
builder.Services.AddSingleton(sp =>
{
    IDeterministicStateStore store = sp.GetRequiredService<IDeterministicStateStore>();
    TimeProvider timeProvider = sp.GetRequiredService<TimeProvider>();
    return new DeterministicEffectStore(store, timeProvider);
});
builder.Services.AddSingleton(sp =>
{
    VersionGate versionGate = sp.GetRequiredService<VersionGate>();
    DeterministicEffectStore effectStore = sp.GetRequiredService<DeterministicEffectStore>();
    return new DeterministicGate(versionGate, effectStore);
});
builder.Services.AddSingleton<TelemetryCalibration>();
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddSingleton<TelemetryStream>();
builder.Services.AddSingleton<TelemetryAlertChannel>();
builder.Services.AddHostedService<TelemetryAggregationWorker>();
builder.Services.AddHostedService<TelemetryAlertService>();

var app = builder.Build();

app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

await app.RunAsync().ConfigureAwait(false);
return;

// static Uri ResolveOtlpEndpoint(string? value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint)
//     ? endpoint
//     : new Uri("http://localhost:4317");

// static bool ResolvePrometheusEnabled(string? value)
// {
//     if (string.IsNullOrWhiteSpace(value))
//     {
//         return true;
//     }

//     return value.Trim() switch
//     {
//         "0" => false,
//         "false" or "False" or "FALSE" => false,
//         _ => true
//     };
// }

sealed partial class TelemetryWorker(
    ILogger<TelemetryWorker> logger,
    TelemetryCalibration calibration,
    TimeProvider timeProvider,
    SafeTaskQueueWrapper<TelemetryWorkItem> queue,
    TaskQueueChannelAdapter<TelemetryWorkItem> adapter,
    TelemetryStream stream,
    TelemetryAlertChannel alerts) : BackgroundService
{
    private readonly ILogger<TelemetryWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TelemetryCalibration _calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly SafeTaskQueueWrapper<TelemetryWorkItem> _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly TaskQueueChannelAdapter<TelemetryWorkItem> _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly TelemetryStream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly TelemetryAlertChannel _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
    private CalibrationProfile? _calibrationProfile;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Result<CalibrationProfile> calibrationResult = await _calibration.RunAsync(stoppingToken).ConfigureAwait(false);
        if (calibrationResult.IsFailure)
        {
            Error error = calibrationResult.Error!;
            LogTelemetryCalibrationFailedCodeMessage(error.Code ?? ErrorCodes.Unspecified, error.Message);
            throw new InvalidOperationException("Telemetry calibration failed – see logs for details.");
        }

        _calibrationProfile = calibrationResult.Value;

        WaitGroup wg = new();

        wg.Go(async ct =>
        {
            int heartbeat = 0;
            while (!ct.IsCancellationRequested)
            {
                heartbeat++;
                TelemetryWorkItem heartbeatItem = new(TelemetryKind.Heartbeat, heartbeat, _timeProvider.GetUtcNow());
                Result<Go.Unit> enqueueResult = await _queue.EnqueueAsync(heartbeatItem, ct).ConfigureAwait(false);
                if (enqueueResult.IsFailure)
                {
                    bool terminate = HandleQueueFailure("enqueue", enqueueResult.Error);
                    if (terminate)
                    {
                        break;
                    }

                    continue;
                }
                await _stream.Writer.WriteAsync(heartbeatItem, ct).ConfigureAwait(false);
                await DelayAsync(TimeSpan.FromSeconds(1), _timeProvider, ct).ConfigureAwait(false);
            }
        }, cancellationToken: stoppingToken);

        wg.Go(async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                double cpuReading = Random.Shared.NextDouble() * 150; // intentionally allow outliers
                TelemetryWorkItem workItem = new(TelemetryKind.Cpu, cpuReading, _timeProvider.GetUtcNow());
                Result<Go.Unit> enqueueResult = await _queue.EnqueueAsync(workItem, ct).ConfigureAwait(false);
                if (enqueueResult.IsFailure)
                {
                    bool terminate = HandleQueueFailure("enqueue", enqueueResult.Error);
                    if (terminate)
                    {
                        break;
                    }

                    continue;
                }
                await _stream.Writer.WriteAsync(workItem, ct).ConfigureAwait(false);
                LogQueuedCpuReadingReadingF2(cpuReading);
                await DelayAsync(TimeSpan.FromMilliseconds(350), _timeProvider, ct).ConfigureAwait(false);
            }
        }, cancellationToken: stoppingToken);

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
        await foreach (TaskQueueLease<TelemetryWorkItem> lease in _adapter.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            SafeTaskQueueLease<TelemetryWorkItem> safeLease = new(lease);
            await ProcessLeaseAsync(safeLease, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ProcessLeaseAsync(SafeTaskQueueLease<TelemetryWorkItem> lease, CancellationToken stoppingToken)
    {
        try
        {
            if (lease is { Attempt: > 1, LastError: not null })
            {
                LogRetryingKindObservedAtObservedatOAttemptAttemptAfterError(lease.Value.Kind, lease.Value.ObservedAt, lease.Attempt, lease.LastError.Message);
            }

            switch (lease.Value.Kind)
            {
                case TelemetryKind.Heartbeat:
                    LogProcessingHeartbeatCount(lease.Value.Value);
                    Result<Go.Unit> completion = await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
                    if (completion.IsFailure)
                    {
                        HandleLeaseFailure("complete", lease, completion.Error);
                    }
                    return;
                case TelemetryKind.Cpu:
                    await HandleCpuReadingAsync(lease, stoppingToken).ConfigureAwait(false);
                    return;
                default:
                    Error unsupported = Error.From("Unsupported telemetry kind.", ErrorCodes.Validation)
                        .WithMetadata("kind", lease.Value.Kind.ToString());
                    Result<Go.Unit> failUnsupported = await lease.FailAsync(unsupported, requeue: false, stoppingToken).ConfigureAwait(false);
                    if (failUnsupported.IsFailure)
                    {
                        HandleLeaseFailure("fail", lease, failUnsupported.Error);
                    }
                    return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Error canceled = Error.From("Processing canceled during shutdown.", ErrorCodes.TaskQueueAbandoned)
                .WithMetadata("kind", lease.Value.Kind.ToString())
                .WithMetadata("attempt", lease.Attempt);

            Result<Go.Unit> failResult = await lease.FailAsync(canceled, requeue: false, CancellationToken.None).ConfigureAwait(false);
            if (failResult.IsFailure)
            {
                HandleLeaseFailure("fail", lease, failResult.Error);
            }
        }
        catch (Exception ex)
        {
            Error failure = Error.From("Telemetry processing encountered an unexpected error.", ErrorCodes.TaskQueueAbandoned)
                .WithMetadata("kind", lease.Value.Kind.ToString())
                .WithMetadata("attempt", lease.Attempt)
                .WithMetadata("exceptionType", ex.GetType().FullName);

            LogUnexpectedFailureProcessingKind(lease.Value.Kind);
            Result<Go.Unit> failResult = await lease.FailAsync(failure, requeue: true, stoppingToken).ConfigureAwait(false);
            if (failResult.IsFailure)
            {
                HandleLeaseFailure("fail", lease, failResult.Error);
            }
        }
    }
    private async ValueTask HandleCpuReadingAsync(SafeTaskQueueLease<TelemetryWorkItem> lease, CancellationToken stoppingToken)
    {
        TelemetryWorkItem message = lease.Value;
        CalibrationProfile profile = _calibrationProfile ?? throw new InvalidOperationException("Calibration profile has not been loaded.");

        // Simulate longer processing that requires periodic heartbeats to keep the lease alive.
        await DelayAsync(TimeSpan.FromSeconds(1.2), _timeProvider, stoppingToken).ConfigureAwait(false);
        Result<Go.Unit> heartbeat = await lease.HeartbeatAsync(stoppingToken).ConfigureAwait(false);
        if (heartbeat.IsFailure)
        {
            HandleLeaseFailure("heartbeat", lease, heartbeat.Error);
            return;
        }

        await DelayAsync(TimeSpan.FromSeconds(0.8), _timeProvider, stoppingToken).ConfigureAwait(false);

        if (message.Value >= 0d && message.Value <= profile.MaxCpuPercent)
        {
            LogCpuReadingValueF2CapturedAtObservedatOProcessedOnAttemptAttemptWithinCalibrated(message.Value, message.ObservedAt, lease.Attempt, profile.MaxCpuPercent);

            if (message.Value >= profile.WarningCpuPercent)
            {
                LogReadingValueF2ExceededWarningThresholdWarningF2CalibrationVVersion(message.Value, profile.WarningCpuPercent, profile.Version);

                await _alerts.PublishAsync(
                        new TelemetryAlert(
                            AlertSeverity.Warning,
                            message,
                            $"Reading {message.Value:F2}% exceeded warning threshold {profile.WarningCpuPercent:F2}% (calibration v{profile.Version})"),
                        stoppingToken)
                    .ConfigureAwait(false);
            }

            Result<Go.Unit> completion = await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
            if (completion.IsFailure)
            {
                HandleLeaseFailure("complete", lease, completion.Error);
            }
            return;
        }

        Error error = Error.From("CPU reading out of range.", ErrorCodes.Validation)
            .WithMetadata("observed", message.Value)
            .WithMetadata("attempt", lease.Attempt)
            .WithMetadata("observedAt", message.ObservedAt);

        bool shouldRetry = lease.Attempt < 3;

        _logger.LogWarning(
            shouldRetry
                ? "Retries remaining for out-of-range CPU reading {Value:F2} (attempt {Attempt})"
                : "Marking out-of-range CPU reading {Value:F2} as dead-letter candidate (attempt {Attempt})",
            message.Value, lease.Attempt);

        await _alerts.PublishAsync(
                new TelemetryAlert(
                    AlertSeverity.Critical,
                    message,
                    $"CPU reading {message.Value:F2}% is outside calibrated range 0 - {profile.MaxCpuPercent:F2}%"),
                stoppingToken)
            .ConfigureAwait(false);

        Result<Go.Unit> failResult = await lease.FailAsync(error, shouldRetry, stoppingToken).ConfigureAwait(false);
        if (failResult.IsFailure)
        {
            HandleLeaseFailure("fail", lease, failResult.Error);
        }
    }

    private bool HandleQueueFailure(string operation, Error? error)
    {
        string code = error?.Code ?? ErrorCodes.Unspecified;
        string message = error?.Message ?? "Unknown error";
        LogQueueOperationFailed(operation, code, message);
        return code is ErrorCodes.TaskQueueDisposed or ErrorCodes.Canceled;
    }

    private void HandleLeaseFailure(string operation, SafeTaskQueueLease<TelemetryWorkItem> lease, Error? error)
    {
        string code = error?.Code ?? ErrorCodes.Unspecified;
        string message = error?.Message ?? "Unknown error";
        LogLeaseOperationFailed(operation, lease.Value.Kind, code, message);
    }

    [LoggerMessage(LogLevel.Error, "Telemetry calibration failed ({code}): {message}")]
    partial void LogTelemetryCalibrationFailedCodeMessage(string code, string message);

    [LoggerMessage(LogLevel.Information, "Queued cpu reading {reading:F2}")]
    partial void LogQueuedCpuReadingReadingF2(double reading);

    [LoggerMessage(LogLevel.Warning, "Retrying {kind} observed at {observedAt:o}: attempt {attempt} after {error}")]
    partial void LogRetryingKindObservedAtObservedatOAttemptAttemptAfterError(TelemetryKind kind, DateTimeOffset observedAt, int attempt, string error);

    [LoggerMessage(LogLevel.Information, "Processing heartbeat {count}")]
    partial void LogProcessingHeartbeatCount(double count);

    [LoggerMessage(LogLevel.Error, "Unexpected failure processing {kind}")]
    partial void LogUnexpectedFailureProcessingKind(TelemetryKind kind);

    [LoggerMessage(LogLevel.Information, "CPU reading {value:F2}%% captured at {observedAt:o} processed on attempt {attempt} within calibrated max {max:F2}")]
    partial void LogCpuReadingValueF2CapturedAtObservedatOProcessedOnAttemptAttemptWithinCalibrated(double value, DateTimeOffset observedAt, int attempt, double max);

    [LoggerMessage(LogLevel.Warning, "Reading {value:F2}%% exceeded warning threshold {warning:F2}%% (calibration v{version})")]
    partial void LogReadingValueF2ExceededWarningThresholdWarningF2CalibrationVVersion(double value, double warning, int version);

    [LoggerMessage(LogLevel.Warning, "Task queue operation '{operation}' failed ({code}): {message}")]
    partial void LogQueueOperationFailed(string operation, string code, string message);

    [LoggerMessage(LogLevel.Warning, "Lease operation '{operation}' for {kind} failed ({code}): {message}")]
    partial void LogLeaseOperationFailed(string operation, TelemetryKind kind, string code, string message);
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
        Result<CalibrationProfile> result = await _gate.Workflow<CalibrationProfile>("telemetry.calibration", 1, 2, _ => 2)
            .ForVersion(1, (ctx, ct) => ctx.CaptureAsync(
                "legacy-profile",
                _ => ValueTask.FromResult(Result.Ok(new CalibrationProfile(100, 90, ctx.Version, _timeProvider.GetUtcNow()))),
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

                    CalibrationProfile profile = new(97.5, 92.5, ctx.Version, _timeProvider.GetUtcNow());
                    return Result.Ok(profile);
                },
                ct))
            .WithFallback((ctx, ct) => ctx.CaptureAsync(
                "fallback-profile",
                _ => ValueTask.FromResult(Result.Ok(new CalibrationProfile(100, 95, ctx.Version, _timeProvider.GetUtcNow()))),
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

sealed class TelemetryStream
{
    private readonly Channel<TelemetryWorkItem> _channel = BoundedChannel<TelemetryWorkItem>(capacity: 256)
        .SingleReader()
        .Build();

    public ChannelWriter<TelemetryWorkItem> Writer => _channel.Writer;

    public ChannelReader<TelemetryWorkItem> Reader => _channel.Reader;
}

sealed partial class TelemetryAggregationWorker(
    TelemetryStream stream,
    TimeProvider timeProvider,
    ILogger<TelemetryAggregationWorker> logger) : BackgroundService
{
    private readonly TelemetryStream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<TelemetryAggregationWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<TelemetryWorkItem> buffer = [];

        while (!stoppingToken.IsCancellationRequested)
        {
            Result<AggregationDecision> decisionResult = await Select<AggregationDecision>(_timeProvider, stoppingToken)
                .Case(_stream.Reader, static (item, _) => Task.FromResult(Result.Ok(AggregationDecision.FromReading(item))))
                .Deadline(FlushInterval, static () => Task.FromResult(Result.Ok(AggregationDecision.Flush())))
                .ExecuteAsync()
                .ConfigureAwait(false);

            if (decisionResult.IsFailure)
            {
                Error? error = decisionResult.Error;
                if (error is { Code: ErrorCodes.SelectDrained })
                {
                    await FlushBufferAsync(buffer, stoppingToken).ConfigureAwait(false);
                    break;
                }

                if (error is { Code: ErrorCodes.Canceled } && stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                LogAggregationLoopEncounteredCodeMessage(error?.Code ?? ErrorCodes.Unspecified, error?.Message ?? "Unknown failure.");
                continue;
            }

            AggregationDecision decision = decisionResult.Value;
            if (decision.HasItem)
            {
                buffer.Add(decision.Item);
            }

            if (decision.ShouldFlush)
            {
                await FlushBufferAsync(buffer, stoppingToken).ConfigureAwait(false);
            }
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            await FlushBufferAsync(buffer, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task FlushBufferAsync(List<TelemetryWorkItem> buffer, CancellationToken stoppingToken)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        TelemetrySummary summary = TelemetrySummary.From(buffer, _timeProvider.GetUtcNow());
        Result<Unit> publishResult = await RetryAsync((attempt, _) =>
                {
                    if (summary.CpuSamples > 0 && Random.Shared.NextDouble() < 0.15)
                    {
                        LogSimulatedSinkBackPressureWhilePublishingSummaryOnAttemptAttempt(attempt);
                        return Task.FromResult(Result.Fail<Unit>(Error.Timeout(TimeSpan.FromSeconds(1))));
                    }

                    LogPublishedTelemetrySummaryCpusamplesCpuSamplesAvgCpuaverageF2MaxCpumaxF2(summary.CpuSamples, summary.CpuAverage, summary.CpuMax, summary.Heartbeats, summary.WindowEnd);
                    return Task.FromResult(Result.Ok(Unit.Value));
                },
                maxAttempts: 3,
                initialDelay: TimeSpan.FromMilliseconds(200),
                timeProvider: _timeProvider,
                logger: _logger,
                cancellationToken: stoppingToken)
            .ConfigureAwait(false);

        if (publishResult.IsFailure)
        {
            Error error = publishResult.Error ?? Error.Unspecified();
            LogFailedToPublishTelemetrySummaryAfterRetriesCodeMessage(error.Code ?? ErrorCodes.Unspecified, error.Message);
        }

        buffer.Clear();
    }

    [LoggerMessage(LogLevel.Warning, "Aggregation loop encountered {code}: {message}")]
    partial void LogAggregationLoopEncounteredCodeMessage(string code, string message);

    [LoggerMessage(LogLevel.Warning, "Simulated sink back-pressure while publishing summary on attempt {attempt}")]
    partial void LogSimulatedSinkBackPressureWhilePublishingSummaryOnAttemptAttempt(int attempt);

    [LoggerMessage(LogLevel.Information, "Published telemetry summary: {cpuSamples} CPU samples (avg {cpuAverage:F2}%% max {cpuMax:F2}%%) across {heartbeats} heartbeats ending {windowEnd:o}")]
    partial void LogPublishedTelemetrySummaryCpusamplesCpuSamplesAvgCpuaverageF2MaxCpumaxF2(int cpuSamples, double cpuAverage, double cpuMax, int heartbeats, DateTimeOffset windowEnd);

    [LoggerMessage(LogLevel.Error, "Failed to publish telemetry summary after retries: {code} - {message}")]
    partial void LogFailedToPublishTelemetrySummaryAfterRetriesCodeMessage(string code, string message);
}

readonly record struct AggregationDecision(bool ShouldFlush, TelemetryWorkItem Item, bool HasItem)
{
    public static AggregationDecision FromReading(TelemetryWorkItem item) => new(false, item, true);

    public static AggregationDecision Flush() => new(true, default, false);
}

readonly record struct TelemetrySummary(int CpuSamples, double CpuAverage, double CpuMax, int Heartbeats, DateTimeOffset WindowEnd)
{
    public static TelemetrySummary From(IReadOnlyList<TelemetryWorkItem> items, DateTimeOffset windowEnd)
    {
        int cpuSamples = 0;
        double cpuSum = 0d;
        double cpuMax = double.MinValue;
        int heartbeats = 0;

        foreach (TelemetryWorkItem item in items)
        {
            switch (item.Kind)
            {
                case TelemetryKind.Cpu:
                    cpuSamples++;
                    cpuSum += item.Value;
                    cpuMax = Math.Max(cpuMax, item.Value);
                    break;
                case TelemetryKind.Heartbeat:
                    heartbeats++;
                    break;
            }
        }

        double average = cpuSamples > 0 ? cpuSum / cpuSamples : 0d;
        double max = cpuSamples > 0 ? cpuMax : 0d;

        return new TelemetrySummary(cpuSamples, average, max, heartbeats, windowEnd);
    }
}

enum AlertSeverity
{
    Critical = 0,
    Warning = 1
}

readonly record struct TelemetryAlert(AlertSeverity Severity, TelemetryWorkItem WorkItem, string Message);

sealed class TelemetryAlertChannel
{
    private readonly PrioritizedChannel<TelemetryAlert> _channel = PrioritizedChannel<TelemetryAlert>(priorityLevels: 2)
        .WithCapacityPerLevel(32)
        .WithDefaultPriority((int)AlertSeverity.Warning)
        .SingleReader()
        .SingleWriter()
        .Build();

    public ChannelReader<TelemetryAlert> Reader => _channel.Reader;

    public ValueTask PublishAsync(TelemetryAlert alert, CancellationToken cancellationToken) =>
        _channel.PrioritizedWriter.WriteAsync(alert, (int)alert.Severity, cancellationToken);
}

sealed class TelemetryAlertService(
    TelemetryAlertChannel alerts,
    ILogger<TelemetryAlertService> logger) : BackgroundService
{
    private readonly TelemetryAlertChannel _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
    private readonly ILogger<TelemetryAlertService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ChannelReader<TelemetryAlert> reader = _alerts.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            TelemetryAlert alert;
            try
            {
                alert = await reader.ReadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            LogLevel level = alert.Severity == AlertSeverity.Critical ? LogLevel.Error : LogLevel.Warning;
            _logger.Log(
                level,
                "Telemetry alert [{Severity}] {Message} (value {Value:F2}%% observed {ObservedAt:o})",
                alert.Severity,
                alert.Message,
                alert.WorkItem.Value,
                alert.WorkItem.ObservedAt);
        }
    }
}

sealed class TelemetryQueueOptions
{
    public int Capacity { get; set; } = 512;
    public double LeaseDurationMs { get; set; } = TimeSpan.FromSeconds(8).TotalMilliseconds;
    public double HeartbeatIntervalMs { get; set; } = TimeSpan.FromSeconds(1).TotalMilliseconds;
    public double LeaseSweepIntervalMs { get; set; } = TimeSpan.FromMilliseconds(500).TotalMilliseconds;
    public double RequeueDelayMs { get; set; } = TimeSpan.FromMilliseconds(750).TotalMilliseconds;
    public int MaxDeliveryAttempts { get; set; } = 3;
}
