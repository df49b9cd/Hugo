using System.Diagnostics;
using System.Diagnostics.Metrics;

using Hugo.TaskQueues.Diagnostics;

using Shouldly;

namespace Hugo.Tests.Diagnostics;

[Collection("TaskQueueConcurrency")]
public class TaskQueueDiagnosticsRegistrationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask AddTaskQueueDiagnostics_ShouldApplyTagEnrichers()
    {
        GoDiagnostics.Reset();

        using var meterListener = new MeterListener();
        var captured = new List<(string Name, TagList Tags)>();
        const string queueName = "dispatch";

        meterListener.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Meter.Name == GoDiagnostics.MeterName &&
                instrument.Name is "taskqueue.enqueued" or "taskqueue.pending")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            captured.Add((instrument.Name, new TagList(tags)));
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
            GoDiagnostics.RecordTaskQueueQueued(queueName, pendingDepth: 1);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline &&
                   (!HasMetric(captured, "taskqueue.enqueued", queueName) ||
                    !HasMetric(captured, "taskqueue.pending", queueName)))
            {
                meterListener.RecordObservableInstruments();
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            meterListener.RecordObservableInstruments();
        }

        GoDiagnostics.Reset();

        captured.ShouldNotBeEmpty();

        Dictionary<string, object?>? enqueuedTags = captured
            .Where(entry => entry.Name == "taskqueue.enqueued")
            .Select(entry => ToDictionary(entry.Tags))
            .FirstOrDefault(entry => entry.TryGetValue("taskqueue.name", out var name) && (string?)name == queueName);

        Dictionary<string, object?>? pendingTags = captured
            .Where(entry => entry.Name == "taskqueue.pending")
            .Select(entry => ToDictionary(entry.Tags))
            .FirstOrDefault(entry => entry.TryGetValue("taskqueue.name", out var name) && (string?)name == queueName);

        enqueuedTags.ShouldNotBeNull($"Expected taskqueue.enqueued measurement for queue '{queueName}'.");
        pendingTags.ShouldNotBeNull($"Expected taskqueue.pending measurement for queue '{queueName}'.");

        AssertTagSet(enqueuedTags!, queueName);
        AssertTagSet(pendingTags!, queueName);
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

    private static void AssertTagSet(Dictionary<string, object?> tags, string queueName)
    {
        tags.ShouldContainKeyAndValue("service.name", "omnirelay.control");
        tags.ShouldContainKeyAndValue("taskqueue.shard", "shard-a");
        tags.ShouldContainKeyAndValue("custom.tag", "diagnostics-test");
        tags.ShouldContainKey("taskqueue.name");
        tags["taskqueue.name"]!.ToString().ShouldBe(queueName);
    }

    private static bool TryGetQueueName(TagList tags, out string? queueName)
    {
        foreach ((string key, object? value) in tags)
        {
            if (key == "taskqueue.name")
            {
                queueName = value?.ToString();
                return true;
            }
        }

        queueName = null;
        return false;
    }

    private static bool HasMetric(IEnumerable<(string Name, TagList Tags)> source, string name, string queueName) =>
        source.Any(entry =>
            entry.Name == name &&
            TryGetQueueName(entry.Tags, out string? capturedName) &&
            capturedName == queueName);
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
