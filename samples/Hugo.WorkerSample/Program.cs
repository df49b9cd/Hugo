using Hugo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Hugo.Go;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();
await app.RunAsync();

sealed class TelemetryWorker(ILogger<TelemetryWorker> logger, TimeProvider timeProvider) : BackgroundService
{
    private readonly ILogger<TelemetryWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = MakeChannel<string>(capacity: 32);
        var wg = new WaitGroup();

        wg.Go(async ct =>
        {
            var heartbeat = 0;
            while (!ct.IsCancellationRequested)
            {
                heartbeat++;
                await channel.Writer.WriteAsync($"heartbeat:{heartbeat}", ct).ConfigureAwait(false);
                await DelayAsync(TimeSpan.FromSeconds(1), _timeProvider, ct).ConfigureAwait(false);
            }
        }, stoppingToken);

        wg.Go(async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                var cpuReading = Random.Shared.NextDouble() * 150; // intentionally allow outliers

                var result = Ok(cpuReading)
                    .Ensure(value => value is >= 0 and <= 100, value => Error.From("cpu reading out of range", ErrorCodes.Validation)
                        .WithMetadata("observed", value))
                    .Map(value => $"cpu:{value:F2}")
                    .Tap(value => _logger.LogInformation("Queued reading {Reading}", value))
                    .TapError(error => _logger.LogWarning("Discarded reading: {Message} (code: {Code})", error.Message, error.Code));

                if (result.IsSuccess)
                {
                    await channel.Writer.WriteAsync(result.Value, ct).ConfigureAwait(false);
                }

                await DelayAsync(TimeSpan.FromMilliseconds(350), _timeProvider, ct).ConfigureAwait(false);
            }
        }, stoppingToken);

        var consumer = Task.Run(async () =>
        {
            await foreach (var message in channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Processing {Message}", message);
            }
        }, stoppingToken);

        try
        {
            await wg.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }
}
