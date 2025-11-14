using System.Diagnostics.Metrics;

using Hugo.TaskQueues;
using Hugo.TaskQueues.Backpressure;
using Hugo.TaskQueues.Diagnostics;
using Hugo.TaskQueues.Replication;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

public class TaskQueueDiagnosticsIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task DiagnosticsHost_ShouldStreamBackpressureAndReplication()
    {
        await using var meterFactory = new TestMeterFactory();
        await using var diagnostics = new TaskQueueDiagnosticsHost(meterFactory, options =>
        {
            options.Metrics.ServiceName = "integration-tests";
            options.Metrics.DefaultShard = "int-shard";
        });

        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "dispatch", Capacity = 8 }, provider);
        await using var monitor = new TaskQueueBackpressureMonitor<int>(queue, new TaskQueueBackpressureMonitorOptions
        {
            HighWatermark = 2,
            LowWatermark = 1,
            Cooldown = TimeSpan.FromMilliseconds(1)
        });
        await using var replicationSource = new TaskQueueReplicationSource<int>(queue);

        using var monitorSubscription = diagnostics.Attach(monitor);
        using var replicationSubscription = diagnostics.Attach(replicationSource);

        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(2, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(3, TestContext.Current.CancellationToken);

        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        var events = new List<TaskQueueDiagnosticsEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reader = diagnostics.Events;

        while (events.Count < 2)
        {
            events.Add(await reader.ReadAsync(cts.Token));
        }

        Assert.Contains(events, evt => evt is TaskQueueBackpressureDiagnosticsEvent);
        Assert.Contains(events, evt => evt is TaskQueueReplicationDiagnosticsEvent replication && replication.QueueName == "dispatch");
    }
    private sealed class TestMeterFactory : IMeterFactory, IDisposable, IAsyncDisposable
    {
        private readonly List<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
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
