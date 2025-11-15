using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;

using Hugo.TaskQueues;
using Hugo.TaskQueues.Diagnostics;

namespace Hugo.Tests.Diagnostics;

public class TaskQueueDiagnosticsRegistrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task AddTaskQueueDiagnostics_ShouldApplyTagEnrichers()
    {
        using var meterListener = new MeterListener();
        var captured = new List<TagList>();

        meterListener.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Meter.Name == GoDiagnostics.MeterName && instrument.Name == "taskqueue.enqueued")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            captured.Add(new TagList(tags));
        });
        meterListener.Start();

        await using var meterFactory = new TestMeterFactory();
        await using (meterFactory.AddTaskQueueDiagnostics(options =>
               {
                   options.Metrics.ServiceName = "omnirelay.control";
                   options.Metrics.DefaultShard = "shard-a";
                   options.Metrics.TagEnrichers.Add((in TaskQueueTagContext ctx, ref TagList tags) =>
                   {
                       tags.Add("custom.tag", "diagnostics-test");
                   });
               }))
        {
            await using var queue = new TaskQueue<string>(new TaskQueueOptions { Name = "dispatch", Capacity = 4 });
            await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
            SpinWait.SpinUntil(() => captured.Count > 0, TimeSpan.FromSeconds(1));
        }

        GoDiagnostics.Reset();

        Assert.NotEmpty(captured);
        var tags = captured.Last(entry => entry.Any(tag => tag.Key == "taskqueue.name" && (string?)tag.Value == "dispatch"));
        Assert.Contains(tags, entry => entry.Key == "service.name" && (string?)entry.Value == "omnirelay.control");
        Assert.Contains(tags, entry => entry.Key == "taskqueue.shard" && (string?)entry.Value == "shard-a");
        Assert.Contains(tags, entry => entry.Key == "custom.tag" && (string?)entry.Value == "diagnostics-test");
    }

    [Fact(Timeout = 15_000)]
    public async Task ConfigureTaskQueueMetrics_ShouldGateThroughputInstruments()
    {
        using var meterListener = new MeterListener();
        var throughputMeasurements = 0;
        var depthMeasurements = 0;

        meterListener.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Meter.Name != GoDiagnostics.MeterName)
            {
                return;
            }

            if (instrument.Name is "taskqueue.enqueued" or "taskqueue.pending")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "taskqueue.enqueued")
            {
                throughputMeasurements += 1;
            }
            else if (instrument.Name == "taskqueue.pending")
            {
                depthMeasurements += 1;
            }
        });
        meterListener.Start();

        using var meter = new Meter(GoDiagnostics.MeterName);
        GoDiagnostics.Configure(meter);
        using (GoDiagnostics.ConfigureTaskQueueMetrics(TaskQueueMetricGroups.QueueDepth))
        {
            await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "dispatch", Capacity = 4 });
            await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
            SpinWait.SpinUntil(() => depthMeasurements > 0, TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, throughputMeasurements);
        Assert.True(depthMeasurements > 0);

        GoDiagnostics.Reset();
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
