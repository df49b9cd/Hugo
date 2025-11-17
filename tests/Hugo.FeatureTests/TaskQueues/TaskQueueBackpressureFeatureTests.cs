using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Shouldly;

using Hugo.TaskQueues.Backpressure;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests.TaskQueues;

[Collection("TaskQueueConcurrency")]
public class TaskQueueBackpressureFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task Monitor_ShouldEmitMetricsAndDiagnostics_DuringChaos()
    {
        using var meterListener = new MeterListener();
        using var meter = new Meter(GoDiagnostics.MeterName);
        var transitions = 0L;
        var pendingSamples = new List<long>();
        var durations = new List<double>();
        long activeDelta = 0;

        meterListener.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Meter.Name != GoDiagnostics.MeterName)
            {
                return;
            }

            if (instrument.Name.StartsWith("hugo.taskqueue.backpressure", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            switch (instrument.Name)
            {
                case "hugo.taskqueue.backpressure.transitions":
                    transitions += measurement;
                    break;
                case "hugo.taskqueue.backpressure.pending":
                    pendingSamples.Add(measurement);
                    break;
                case "hugo.taskqueue.backpressure.active":
                    activeDelta += measurement;
                    break;
            }
        });

        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "hugo.taskqueue.backpressure.duration")
            {
                durations.Add(measurement);
            }
        });

        meterListener.Start();
        GoDiagnostics.Configure(meter);

        try
        {
            var provider = new FakeTimeProvider();
            await using var queue = new TaskQueue<int>(new TaskQueueOptions { Capacity = 64 }, provider);
            await using var monitor = new TaskQueueBackpressureMonitor<int>(queue, new TaskQueueBackpressureMonitorOptions
            {
                HighWatermark = 5,
                LowWatermark = 2,
                Cooldown = TimeSpan.FromMilliseconds(2)
            });
            await using var diagnostics = new TaskQueueBackpressureDiagnosticsListener();
            using var subscription = monitor.RegisterListener(diagnostics);

            var producer = Task.Run(async () =>
            {
                for (var wave = 0; wave < 3; wave++)
                {
                    for (var i = 0; i < 6; i++)
                    {
                        await queue.EnqueueAsync(wave * 10 + i, TestContext.Current.CancellationToken);
                    }

                    provider.Advance(TimeSpan.FromMilliseconds(3));
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken);

            var consumer = Task.Run(async () =>
            {
                for (var i = 0; i < 18; i++)
                {
                    var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
                    await Task.Delay(5, TestContext.Current.CancellationToken);
                    await lease.CompleteAsync(TestContext.Current.CancellationToken);
                    provider.Advance(TimeSpan.FromMilliseconds(4));
                }
            }, TestContext.Current.CancellationToken);

            await Task.WhenAll(producer, consumer);
            await monitor.WaitForDrainingAsync(TestContext.Current.CancellationToken);

            transitions >= 2.ShouldBeTrue();
            pendingSamples.ShouldNotBeEmpty();
            durations.ShouldNotBeEmpty();
            activeDelta.ShouldBe(0);

            var snapshot = diagnostics.Latest;
            snapshot.IsActive.ShouldBeFalse();
            snapshot.PendingCount <= 2.ShouldBeTrue();
        }
        finally
        {
            GoDiagnostics.Reset();
        }
    }
}
