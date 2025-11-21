using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;

using Hugo.TaskQueues.Diagnostics;


namespace Hugo.Tests.TaskQueues;

[Collection("TaskQueueConcurrency")]
public sealed class TaskQueueDiagnosticsTagFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task TaskQueueDiagnostics_ShouldApplyCustomTagEnrichers()
    {
        GoDiagnostics.Reset();
        await using var meterFactory = new TestMeterFactory();

        List<ReadOnlyCollection<KeyValuePair<string, object?>>> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GoDiagnostics.MeterName && instrument.Name == "taskqueue.enqueued")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            measurements.Add(tags.ToArray().AsReadOnly());
        });
        listener.Start();

        await using var registration = meterFactory.AddTaskQueueDiagnostics(options =>
        {
            options.Metrics.ServiceName = "feature-service";
            options.Metrics.DefaultShard = "feature-shard";
            options.Metrics.TagEnrichers.Add((in TaskQueueTagContext context, ref TagList tags) =>
            {
                tags.Add("queue.from.context", context.QueueName ?? string.Empty);
            });
        });

        const string queueName = "feature-queue";
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = 8,
            Name = queueName
        });

        await queue.EnqueueAsync(7, TestContext.Current.CancellationToken);

        listener.RecordObservableInstruments();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        measurements.ShouldNotBeEmpty();
        Dictionary<string, object?> tagSet = measurements
            .Select(ToDictionary)
            .LastOrDefault(tags => tags.TryGetValue("taskqueue.name", out var name) && (string?)name == queueName)
            ?? ToDictionary(measurements.Last());

        tagSet.ShouldContainKeyAndValue("taskqueue.name", queueName);
        tagSet.ShouldContainKeyAndValue("service.name", "feature-service");
        tagSet.ShouldContainKeyAndValue("taskqueue.shard", "feature-shard");
        tagSet.ShouldContainKeyAndValue("queue.from.context", queueName);
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

        public void Dispose()
        {
            foreach (Meter meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static Dictionary<string, object?> ToDictionary(IEnumerable<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, object?> dict = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }

        return dict;
    }
}
