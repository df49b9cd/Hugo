using System.Diagnostics;
using System.Reflection;

using Shouldly;

namespace Hugo.Tests;

[Collection("GoConcurrency")]
public class RateLimitedSamplingFeatureTests
{
    [Fact(Timeout = 15_000)]
    public void UseRateLimitedSampling_ShouldLimitActivitiesPerInterval()
    {
        GoDiagnostics.Reset();

        using var source = GoDiagnostics.CreateActivitySource(
            name: "rate-limit.feature",
            version: GoDiagnostics.InstrumentationVersion,
            schemaUrl: GoDiagnostics.TelemetrySchemaUrl);
        GoDiagnostics.Configure(source);

        var subscriptionType = typeof(GoDiagnostics).GetNestedType("RateLimitedSamplingSubscription", BindingFlags.NonPublic);
        subscriptionType.ShouldNotBeNull();

        var ctor = subscriptionType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(ActivitySource), typeof(int), typeof(TimeSpan), typeof(ActivitySamplingResult) },
            modifiers: null);

        ctor.ShouldNotBeNull();

        using var subscription = (IDisposable)ctor!.Invoke(new object[]
        {
            source,
            2,
            TimeSpan.FromMilliseconds(200),
            ActivitySamplingResult.PropagationData
        });

        var acquireSlot = subscriptionType.GetMethod("AcquireSlot", BindingFlags.Instance | BindingFlags.NonPublic);
        acquireSlot.ShouldNotBeNull();

        ActivitySamplingResult Next() => (ActivitySamplingResult)acquireSlot!.Invoke(subscription, null)!;

        Next().ShouldBe(ActivitySamplingResult.AllData);
        Next().ShouldBe(ActivitySamplingResult.AllData);
        Next().ShouldBe(ActivitySamplingResult.PropagationData);

        Thread.Sleep(220);

        Next().ShouldBe(ActivitySamplingResult.AllData);

        GoDiagnostics.Reset();
    }
}
