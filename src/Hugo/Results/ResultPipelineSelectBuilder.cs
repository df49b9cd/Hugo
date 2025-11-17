using System.Threading.Channels;
using Hugo.Policies;

namespace Hugo;

/// <summary>
/// Pipeline-aware wrapper over <see cref="SelectBuilder{TResult}"/> that absorbs compensation scopes automatically.
/// </summary>
public sealed class ResultPipelineSelectBuilder<TResult>
{
    private readonly ResultPipelineStepContext _context;
    private readonly SelectBuilder<TResult> _builder;
    private readonly CancellationTokenSource? _linkedCts;

    internal ResultPipelineSelectBuilder(ResultPipelineStepContext context, TimeSpan timeout, TimeProvider? provider, CancellationToken token)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        var effectiveProvider = provider ?? context.TimeProvider;
        var (effectiveToken, linkedCts) = LinkToken(context.CancellationToken, token);
        _linkedCts = linkedCts;
        _builder = new SelectBuilder<TResult>(timeout, effectiveProvider, effectiveToken);
    }

    public ResultPipelineSelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue)
    {
        _builder.CaseValueTask(reader, Wrap(onValue));
        return this;
    }

    public ResultPipelineSelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue)
    {
        _builder.CaseValueTask(reader, priority, Wrap(onValue));
        return this;
    }

    public ResultPipelineSelectBuilder<TResult> Default(Func<CancellationToken, ValueTask<Result<TResult>>> onDefault)
    {
        _builder.DefaultValueTask(Wrap(onDefault));
        return this;
    }

    public async ValueTask<Result<TResult>> ExecuteAsync()
    {
        try
        {
            var result = await _builder.ExecuteAsync().ConfigureAwait(false);
            _context.AbsorbResult(result);
            return result;
        }
        finally
        {
            _linkedCts?.Dispose();
        }
    }

    private Func<T, CancellationToken, ValueTask<Result<TResult>>> Wrap<T>(Func<T, CancellationToken, ValueTask<Result<TResult>>> handler) =>
        async (value, token) =>
        {
            var result = await handler(value, token).ConfigureAwait(false);
            _context.AbsorbResult(result);
            return result;
        };

    private Func<CancellationToken, ValueTask<Result<TResult>>> Wrap(Func<CancellationToken, ValueTask<Result<TResult>>> handler) =>
        async token =>
        {
            var result = await handler(token).ConfigureAwait(false);
            _context.AbsorbResult(result);
            return result;
        };

    private static (CancellationToken Token, CancellationTokenSource? Source) LinkToken(CancellationToken primary, CancellationToken secondary)
    {
        if (!secondary.CanBeCanceled)
        {
            return (primary, null);
        }

        if (!primary.CanBeCanceled)
        {
            return (secondary, null);
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(primary, secondary);
        return (linked.Token, linked);
    }
}
