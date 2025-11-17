using System.Diagnostics.Metrics;
using System.Text.Json;
using Shouldly;

using Hugo.TaskQueues.Backpressure;
using Hugo.TaskQueues.Diagnostics;
using Hugo.TaskQueues.Replication;

namespace Hugo.Tests.TaskQueues;

public class TaskQueueDiagnosticsFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task DiagnosticsHost_ShouldProduceControlPlanePayloads()
    {
        await using var meterFactory = new TestMeterFactory();
        await using var diagnostics = new TaskQueueDiagnosticsHost(meterFactory, options =>
        {
            options.Metrics.ServiceName = "feature-tests";
            options.Metrics.DefaultShard = "feature-shard";
        });

        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "dispatch", Capacity = 16 });
        await using var monitor = new TaskQueueBackpressureMonitor<int>(queue, new TaskQueueBackpressureMonitorOptions
        {
            HighWatermark = 4,
            LowWatermark = 2,
            Cooldown = TimeSpan.FromMilliseconds(1)
        });
        await using var replicationSource = new TaskQueueReplicationSource<int>(queue);

        using var monitorSubscription = diagnostics.Attach(monitor);
        using var replicationSubscription = diagnostics.Attach(replicationSource);

        for (var i = 0; i < 6; i++)
        {
            await queue.EnqueueAsync(i, TestContext.Current.CancellationToken);
        }

        var failedLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await failedLease.FailAsync(Error.From("Simulated failure", "taskqueue.sample.failure"), cancellationToken: TestContext.Current.CancellationToken);

        var completedLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await completedLease.CompleteAsync(TestContext.Current.CancellationToken);

        var events = new List<TaskQueueDiagnosticsEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (events.Count < 3)
        {
            events.Add(await diagnostics.Events.ReadAsync(cts.Token));
        }

        var replication = events.First(evt => evt is TaskQueueReplicationDiagnosticsEvent).ShouldBeOfType<TaskQueueReplicationDiagnosticsEvent>();
        replication.QueueName.ShouldBe("dispatch");
        (replication.SequenceNumber >= 1).ShouldBeTrue();
        (replication.SequenceDelta >= 1).ShouldBeTrue();
        (replication.WallClockLag >= TimeSpan.Zero).ShouldBeTrue();

        var serialized = JsonSerializer.Serialize(replication, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        serialized.ShouldContain("\"queueName\":\"dispatch\"");
        serialized.ShouldContain("\"sequenceNumber\":");
        serialized.ShouldContain("\"flags\":");
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
