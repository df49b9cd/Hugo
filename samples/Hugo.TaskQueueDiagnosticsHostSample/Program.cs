using System.Diagnostics.Metrics;
using System.Text.Json;

using Hugo;
using Hugo.TaskQueues.Backpressure;
using Hugo.TaskQueues.Diagnostics;
using Hugo.TaskQueues.Replication;

JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IMeterFactory, DefaultMeterFactory>();
builder.Services.AddSingleton(sp => new TaskQueue<int>(new TaskQueueOptions
{
    Name = "omnirelay.dispatch",
    Capacity = 256,
    LeaseDuration = TimeSpan.FromSeconds(30),
    RequeueDelay = TimeSpan.FromSeconds(2)
}, deadLetter: static (_, _) => ValueTask.CompletedTask));
builder.Services.AddSingleton<TaskQueueBackpressureMonitor<int>>(sp =>
{
    var queue = sp.GetRequiredService<TaskQueue<int>>();
    return new TaskQueueBackpressureMonitor<int>(queue, new TaskQueueBackpressureMonitorOptions
    {
        HighWatermark = 128,
        LowWatermark = 32,
        Cooldown = TimeSpan.FromSeconds(5)
    });
});
builder.Services.AddSingleton<TaskQueueReplicationSource<int>>(sp =>
{
    var queue = sp.GetRequiredService<TaskQueue<int>>();
    return new TaskQueueReplicationSource<int>(queue);
});
builder.Services.AddSingleton(sp =>
{
    var meterFactory = sp.GetRequiredService<IMeterFactory>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    return new TaskQueueDiagnosticsHost(meterFactory, options =>
    {
        options.Metrics.ServiceName = env.ApplicationName;
        options.Metrics.DefaultShard = Environment.MachineName;
        options.Metrics.DefaultTags["taskqueue.team"] = "omnirelay";
    });
});
builder.Services.AddHostedService<TaskQueueDiagnosticsBootstrapper>();
builder.Services.AddHostedService<TaskQueueWorkloadService>();

var app = builder.Build();

app.MapGet("/", () => Results.Text("TaskQueue diagnostics host ready. Stream /diagnostics/taskqueue for SSE output."));

app.MapGet("/diagnostics/taskqueue", async (TaskQueueDiagnosticsHost diagnostics, HttpResponse response, CancellationToken token) =>
{
    response.Headers.CacheControl = "no-store";
    response.Headers["Content-Type"] = "text/event-stream";

    await foreach (TaskQueueDiagnosticsEvent evt in diagnostics.Events.ReadAllAsync(token))
    {
        string payload = JsonSerializer.Serialize(evt, serializerOptions);
        await response.WriteAsync($"data: {payload}\n\n", token);
        await response.Body.FlushAsync(token);
    }
});

await app.RunAsync();

sealed class TaskQueueDiagnosticsBootstrapper(
    TaskQueueDiagnosticsHost diagnostics,
    TaskQueueBackpressureMonitor<int> monitor,
    TaskQueueReplicationSource<int> replicationSource) : IHostedService
{
    private IDisposable? _monitorSubscription;
    private IDisposable? _replicationSubscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitorSubscription = diagnostics.Attach(monitor);
        _replicationSubscription = diagnostics.Attach(replicationSource);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _monitorSubscription?.Dispose();
        _replicationSubscription?.Dispose();
        return Task.CompletedTask;
    }
}

sealed class TaskQueueWorkloadService : BackgroundService
{
    private readonly TaskQueue<int> _queue;
    private readonly ILogger<TaskQueueWorkloadService> _logger;
    private readonly Random _random = new();

    public TaskQueueWorkloadService(TaskQueue<int> queue, ILogger<TaskQueueWorkloadService> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProduceAsync(stoppingToken);
            await ConsumeAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }

    private async Task ProduceAsync(CancellationToken token)
    {
        for (var i = 0; i < 48; i++)
        {
            await _queue.EnqueueAsync(_random.Next(0, 10_000), token);
        }
    }

    private async Task ConsumeAsync(CancellationToken token)
    {
        var processed = 0;
        while (processed < 32 && !token.IsCancellationRequested)
        {
            TaskQueueLease<int> lease;
            try
            {
                lease = await _queue.LeaseAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(25, 125)), token);

            if (_random.NextDouble() < 0.15)
            {
                var error = Error.From("Simulated failure during diagnostics sample processing.", "diagnostics.failure");
                await lease.FailAsync(error, requeue: true, token);
                _logger.LogWarning("Simulated failure for sequence {SequenceId}", lease.SequenceId);
            }
            else
            {
                await lease.CompleteAsync(token);
            }

            processed++;
        }
    }
}

sealed class DefaultMeterFactory : IMeterFactory, IDisposable
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
