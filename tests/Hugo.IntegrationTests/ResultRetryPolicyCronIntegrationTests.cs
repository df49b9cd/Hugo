using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests;

public sealed class ResultRetryPolicyCronIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task EvaluateAsync_ShouldScheduleNextCronOccurrence()
    {
        var provider = new FakeTimeProvider();
        provider.SetUtcNow(new DateTimeOffset(2025, 3, 1, 1, 0, 0, TimeSpan.Zero));

        var policy = ResultRetryPolicy.Cron("2 30 * * *", TimeZoneInfo.Utc);
        var state = policy.CreateState(provider);

        var decision = await policy.EvaluateAsync(state, Error.From("failure", "policy.cron.test"), TestContext.Current.CancellationToken);

        decision.ShouldRetry.ShouldBeTrue();
        decision.ScheduledAt.ShouldNotBeNull();
        decision.ScheduledAt!.Value.ShouldBe(new DateTimeOffset(2025, 3, 1, 2, 30, 0, TimeSpan.Zero));
        state.Attempt.ShouldBe(1);
        state.LastAttemptAt.ShouldBe(provider.GetUtcNow());
    }
}
