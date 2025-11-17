using System.Threading;
using System.Threading.Channels;

using Hugo.Policies;

using Unit = Hugo.Go.Unit;

namespace Hugo;

/// <summary>
/// Timer helpers scoped to a <see cref="ResultPipelineStepContext"/>.
/// </summary>
public static class ResultPipelineTimers
{
    public static async ValueTask<Result<Unit>> DelayAsync(
        ResultPipelineStepContext context,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var linkedCts = LinkToken(context.CancellationToken, cancellationToken, out var token);
        try
        {
            await Go.DelayAsync(delay, context.TimeProvider, token).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<Unit>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    public static async ValueTask<Result<DateTimeOffset>> AfterAsync(
        ResultPipelineStepContext context,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var linkedCts = LinkToken(context.CancellationToken, cancellationToken, out var token);
        try
        {
            var timestamp = await Go.AfterValueTaskAsync(delay, context.TimeProvider, token).ConfigureAwait(false);
            return Result.Ok(timestamp);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<DateTimeOffset>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    public static Go.GoTicker NewTicker(
        ResultPipelineStepContext context,
        TimeSpan period,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var linkedCts = LinkToken(context.CancellationToken, cancellationToken, out var token);
        var ticker = Go.NewTicker(period, context.TimeProvider, token);
        if (linkedCts is not null)
        {
            context.RegisterCompensation(linkedCts, static (cts, _) =>
            {
                cts.Cancel();
                cts.Dispose();
                return ValueTask.CompletedTask;
            });
        }
        context.RegisterCompensation(async ct =>
        {
            await ticker.DisposeAsync().ConfigureAwait(false);
        });
        return ticker;
    }

    public static ChannelReader<DateTimeOffset> Tick(
        ResultPipelineStepContext context,
        TimeSpan period,
        CancellationToken cancellationToken = default) =>
        NewTicker(context, period, cancellationToken).Reader;

    private static CancellationTokenSource? LinkToken(CancellationToken primary, CancellationToken secondary, out CancellationToken token)
    {
        if (!secondary.CanBeCanceled)
        {
            token = primary;
            return null;
        }

        if (!primary.CanBeCanceled)
        {
            token = secondary;
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(primary, secondary);
        token = linked.Token;
        return linked;
    }
}
