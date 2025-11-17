using Hugo.Policies;

using Shouldly;

namespace Hugo.Tests.Policies;

public sealed class CronScheduleTests
{
    [Fact(Timeout = 15_000)]
    public void Parse_ShouldThrow_WhenSegmentCountInvalid()
    {
        Should.Throw<FormatException>(() =>
        {
            var schedule = CronSchedule.Parse("*/5 */5 */5", TimeZoneInfo.Utc);
            _ = schedule.GetNextOccurrence(DateTimeOffset.UtcNow, TimeProvider.System);
        });
    }

    [Fact(Timeout = 15_000)]
    public void GetNextOccurrence_ShouldAdvanceToSpecifiedHourAndMinute()
    {
        var schedule = CronSchedule.Parse("10 30 * * *", TimeZoneInfo.Utc);
        DateTimeOffset reference = new(2025, 1, 1, 9, 25, 0, TimeSpan.Zero);

        DateTimeOffset? next = schedule.GetNextOccurrence(reference, TimeProvider.System);

        next.ShouldNotBeNull();
        next?.Hour.ShouldBe(10);
        next?.Minute.ShouldBe(30);
        next?.Date.ShouldBe(reference.Date);
    }

    [Fact(Timeout = 15_000)]
    public void GetNextOccurrence_ShouldRespectStepExpressions()
    {
        var schedule = CronSchedule.Parse("* */5 * * *", TimeZoneInfo.Utc);
        DateTimeOffset reference = new(2025, 1, 1, 14, 12, 0, TimeSpan.Zero);

        DateTimeOffset? next = schedule.GetNextOccurrence(reference, TimeProvider.System);

        next.ShouldNotBeNull();
        next?.Hour.ShouldBe(reference.Hour);
        next?.Minute.ShouldBe(17); // 12 + 5 step
    }

    [Fact(Timeout = 15_000)]
    public void GetNextOccurrence_ShouldClampOutOfRangeFields()
    {
        var schedule = CronSchedule.Parse("99 99 31 15 *", TimeZoneInfo.Utc);
        DateTimeOffset reference = new(2025, 7, 10, 8, 0, 0, TimeSpan.Zero);

        DateTimeOffset? next = schedule.GetNextOccurrence(reference, TimeProvider.System);

        next.ShouldNotBeNull();
        next?.Hour.ShouldBe(23);
        next?.Minute.ShouldBe(59);
        next?.Month.ShouldBe(12);
        next?.Day.ShouldBe(31);
    }
}
