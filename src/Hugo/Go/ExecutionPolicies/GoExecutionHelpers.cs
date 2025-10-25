namespace Hugo;

internal static class GoExecutionHelpers
{
    public static CancellationToken? ResolveCancellationToken(CancellationToken preferred, CancellationToken alternate) =>
        preferred.CanBeCanceled ? preferred : alternate.CanBeCanceled ? alternate : null;
}
