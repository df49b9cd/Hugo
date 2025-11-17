using System.Diagnostics;
using System.Diagnostics.Metrics;

using Shouldly;

using Hugo.TaskQueues.Diagnostics;

namespace Hugo.Tests.Diagnostics;

[Collection("TaskQueueConcurrency")]
public class TaskQueueDiagnosticsRegistrationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask AddTaskQueueDiagnostics_ShouldApplyTagEnrichers()
    {
        GoDiagnostics.Reset();

        using var meterListener = new MeterListener();
        var captured = new List<TagList>();
        const string queueName = "dispatch";

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
            await using var queue = new TaskQueue<string>(new TaskQueueOptions { Name = queueName, Capacity = 4 });
            await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
            meterListener.RecordObservableInstruments();
            SpinWait.SpinUntil(() => captured.Count > 0, TimeSpan.FromSeconds(2));
        }

        GoDiagnostics.Reset();

        captured.ShouldNotBeEmpty();
        Dictionary<string, object?> tags = captured
            .Select(ToDictionary)
            .LastOrDefault(entry => entry.TryGetValue("taskqueue.name", out var name) && (string?)name == queueName)
            ?? ToDictionary(captured.Last());

        tags.ShouldContainKeyAndValue("service.name", "omnirelay.control");
        tags.ShouldContainKeyAndValue("taskqueue.shard", "shard-a");
        tags.ShouldContainKeyAndValue("custom.tag", "diagnostics-test");
        tags.ShouldContainKey("taskqueue.name");
        tags["taskqueue.name"]!.ToString().ShouldBe(queueName);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ConfigureTaskQueueMetrics_ShouldGateThroughputInstruments()
    {
        GoDiagnostics.Reset();

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

        throughputMeasurements.ShouldBe(0);
        (depthMeasurements > 0).ShouldBeTrue();

        GoDiagnostics.Reset();
    }

    private static Dictionary<string, object?> ToDictionary(TagList tags)
    {
        Dictionary<string, object?> dict = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }

        return dict;
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
