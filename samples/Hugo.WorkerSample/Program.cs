using Hugo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Hugo.Go;

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddHostedService<TelemetryWorker>();

var app = builder.Build();
await app.RunAsync();

sealed class TelemetryWorker(
    ILogger<TelemetryWorker> logger,
    TimeProvider timeProvider,
    TaskQueue<TelemetryWorkItem> queue) : BackgroundService
{
    private readonly ILogger<TelemetryWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TaskQueue<TelemetryWorkItem> _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        // Simulate longer processing that requires periodic heartbeats to keep the lease alive.
        await DelayAsync(TimeSpan.FromSeconds(1.2), _timeProvider, stoppingToken).ConfigureAwait(false);
        await lease.HeartbeatAsync(stoppingToken).ConfigureAwait(false);
        await DelayAsync(TimeSpan.FromSeconds(0.8), _timeProvider, stoppingToken).ConfigureAwait(false);

        if (message.Value is >= 0 and <= 100)
        {
            _logger.LogInformation(
                "CPU reading {Value:F2}%% captured at {ObservedAt:o} processed on attempt {Attempt}",
                message.Value,
                message.ObservedAt,
                lease.Attempt);

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
