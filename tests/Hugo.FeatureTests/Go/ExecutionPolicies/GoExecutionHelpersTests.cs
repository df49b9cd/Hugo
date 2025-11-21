
using static Hugo.GoExecutionHelpers;

namespace Hugo.Tests;

public class GoExecutionHelpersTests
{
    [Fact(Timeout = 15_000)]
    public void ResolveCancellationToken_ReturnsPreferred_WhenPreferredCancelable()
    {
        using var preferred = new CancellationTokenSource();
        using var alternate = new CancellationTokenSource();

        CancellationToken? resolved = ResolveCancellationToken(preferred.Token, alternate.Token);

        resolved.HasValue.ShouldBeTrue();
        CancellationToken preferredToken = resolved!.Value;
        preferredToken.ShouldBe(preferred.Token);
    }

    [Fact(Timeout = 15_000)]
    public void ResolveCancellationToken_ReturnsAlternate_WhenPreferredNotCancelable()
    {
        using var alternate = new CancellationTokenSource();

        CancellationToken? resolved = ResolveCancellationToken(CancellationToken.None, alternate.Token);

        resolved.HasValue.ShouldBeTrue();
        CancellationToken resolvedToken = resolved!.Value;
        resolvedToken.ShouldBe(alternate.Token);
    }

    [Fact(Timeout = 15_000)]
    public void ResolveCancellationToken_ReturnsNull_WhenNeitherCancelable()
    {
        CancellationToken? resolved = ResolveCancellationToken(CancellationToken.None, CancellationToken.None);

        resolved.HasValue.ShouldBeFalse();
    }
}
