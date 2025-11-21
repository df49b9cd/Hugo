using System.Diagnostics.Metrics;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = diagnostics.Events;
        var observedBackpressure = false;
        var observedReplication = false;

        while (!observedBackpressure || !observedReplication)
        {
            TaskQueueDiagnosticsEvent diagnosticsEvent = await reader.ReadAsync(cts.Token);
            events.Add(diagnosticsEvent);

            observedBackpressure |= diagnosticsEvent is TaskQueueBackpressureDiagnosticsEvent;
            observedReplication |= diagnosticsEvent is TaskQueueReplicationDiagnosticsEvent replication && replication.QueueName == "dispatch";
        }

        observedBackpressure.ShouldBeTrue("Expected to observe at least one backpressure diagnostics event.");
        observedReplication.ShouldBeTrue("Expected to observe at least one replication diagnostics event for queue 'dispatch'.");
    }

    [Fact(Timeout = 15_000)]
    public async Task DiagnosticsHost_ShouldCompleteStreamOnDispose()
    {
        await using var meterFactory = new TestMeterFactory();
        await using var diagnostics = new TaskQueueDiagnosticsHost(meterFactory);

        var signal = new TaskQueueBackpressureSignal(true, 2, 4, 1, DateTimeOffset.UtcNow);
        await diagnostics.BackpressureListener.OnSignalAsync(signal, TestContext.Current.CancellationToken);

        TaskQueueDiagnosticsEvent evt = await diagnostics.Events.ReadAsync(TestContext.Current.CancellationToken);
        evt.ShouldBeOfType<TaskQueueBackpressureDiagnosticsEvent>().Signal.ShouldBe(signal);

        await diagnostics.DisposeAsync();
        await diagnostics.Events.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }
    private sealed class TestMeterFactory : IMeterFactory, IDisposable, IAsyncDisposable
    {
        private readonly List<Meter> _meters = [];

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
