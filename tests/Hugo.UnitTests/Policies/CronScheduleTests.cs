using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests.Policies;

public sealed class CronScheduleTests
{
    [Fact(Timeout = 15_000)]
    public void GetNextOccurrence_ShouldClampDayAndMonth()
    {
        var provider = new FakeTimeProvider();
        provider.SetUtcNow(new DateTimeOffset(2025, 2, 15, 4, 0, 0, TimeSpan.Zero));

        var schedule = CronSchedule.Parse("5 10 31 2 *", TimeZoneInfo.Utc);

        DateTimeOffset? next = schedule.GetNextOccurrence(provider.GetUtcNow(), provider);

        next.ShouldNotBeNull();
        next!.Value.ShouldBe(new DateTimeOffset(2025, 2, 28, 5, 10, 0, TimeSpan.Zero));
    }

    [Fact(Timeout = 15_000)]
    public void GetNextOccurrence_ShouldWrapSteppedMinute()
    {
        var provider = new FakeTimeProvider();
        provider.SetUtcNow(new DateTimeOffset(2025, 1, 1, 0, 50, 0, TimeSpan.Zero));

        var schedule = CronSchedule.Parse("0 */15 1 1 *", TimeZoneInfo.Utc);

        DateTimeOffset? next = schedule.GetNextOccurrence(provider.GetUtcNow(), provider);

        next.ShouldNotBeNull();
        next!.Value.ShouldBe(new DateTimeOffset(2025, 1, 1, 0, 1, 0, TimeSpan.Zero));
    }

    [Fact(Timeout = 15_000)]
    public void Parse_ShouldThrowOnInvalidSegmentCount()
    {
        Should.Throw<FormatException>(() =>
        {
            var schedule = CronSchedule.Parse("*/5", TimeZoneInfo.Utc);
            schedule.GetNextOccurrence(DateTimeOffset.UtcNow, TimeProvider.System);
        });
    }

    [Fact(Timeout = 15_000)]
    public void Parse_ShouldRejectEmptyExpression()
    {
        Should.Throw<ArgumentException>(() => CronSchedule.Parse("  ", TimeZoneInfo.Utc));
    }
}
