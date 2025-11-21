using System.Diagnostics.Metrics;
using System.Text.Json;

using Hugo.TaskQueues;
using Hugo.TaskQueues.Backpressure;
using Hugo.TaskQueues.Diagnostics;
using Hugo.TaskQueues.Replication;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests.TaskQueues;

public class TaskQueueDiagnosticsFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask DiagnosticsHost_ShouldProduceControlPlanePayloads()
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

    [Fact(Timeout = 15_000)]
    public async ValueTask DiagnosticsHost_ShouldComputeSequenceDelta()
    {
        await using var meterFactory = new TestMeterFactory();
        await using var diagnostics = new TaskQueueDiagnosticsHost(meterFactory);

        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "diagnostics.replication", Capacity = 8 }, provider);
        await using var replicationSource = new TaskQueueReplicationSource<int>(queue, new TaskQueueReplicationSourceOptions<int>
        {
            TimeProvider = provider,
            SourcePeerId = "origin"
        });

        using var subscription = diagnostics.Attach(replicationSource);
        var listener = (ITaskQueueLifecycleListener<int>)replicationSource;

        DateTimeOffset occurred = provider.GetUtcNow();
        listener.OnEvent(new TaskQueueLifecycleEvent<int>(
            EventId: 1,
            Kind: TaskQueueLifecycleEventKind.Enqueued,
            queue: queue.QueueName,
            SequenceId: 1,
            Attempt: 1,
            OccurredAt: occurred,
            EnqueuedAt: occurred,
            Value: 99,
            Error: null,
            OwnershipToken: null,
            LeaseExpiration: null,
            Flags: TaskQueueLifecycleEventMetadata.None));

        provider.Advance(TimeSpan.FromMilliseconds(5));
        occurred = provider.GetUtcNow();

        listener.OnEvent(new TaskQueueLifecycleEvent<int>(
            EventId: 2,
            Kind: TaskQueueLifecycleEventKind.Completed,
            queue: queue.QueueName,
            SequenceId: 2,
            Attempt: 1,
            OccurredAt: occurred,
            EnqueuedAt: occurred,
            Value: 99,
            Error: null,
            OwnershipToken: null,
            LeaseExpiration: null,
            Flags: TaskQueueLifecycleEventMetadata.None));

        List<TaskQueueReplicationDiagnosticsEvent> diagnosticsEvents = [];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (diagnosticsEvents.Count < 2)
        {
            TaskQueueDiagnosticsEvent evt = await diagnostics.Events.ReadAsync(cts.Token);
            if (evt is TaskQueueReplicationDiagnosticsEvent replication)
            {
                diagnosticsEvents.Add(replication);
            }
        }

        diagnosticsEvents[0].SequenceDelta.ShouldBe(1);
        diagnosticsEvents[1].SequenceDelta.ShouldBe(1);
        diagnosticsEvents[0].ObservedAt.ShouldBe(diagnosticsEvents[0].RecordedAt);
        diagnosticsEvents[0].WallClockLag.ShouldBe(TimeSpan.Zero);

        await diagnostics.DisposeAsync();
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
