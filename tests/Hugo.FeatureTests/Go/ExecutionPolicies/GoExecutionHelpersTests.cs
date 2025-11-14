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

        Assert.True(resolved.HasValue);
        Assert.Equal(preferred.Token, resolved.Value);
    }

    [Fact(Timeout = 15_000)]
    public void ResolveCancellationToken_ReturnsAlternate_WhenPreferredNotCancelable()
    {
        using var alternate = new CancellationTokenSource();

        CancellationToken? resolved = ResolveCancellationToken(CancellationToken.None, alternate.Token);

        Assert.True(resolved.HasValue);
        Assert.Equal(alternate.Token, resolved.Value);
    }

    [Fact(Timeout = 15_000)]
    public void ResolveCancellationToken_ReturnsNull_WhenNeitherCancelable()
    {
        CancellationToken? resolved = ResolveCancellationToken(CancellationToken.None, CancellationToken.None);

        Assert.False(resolved.HasValue);
    }
}
