using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Fluent builder for typed channel select operations with priorities, deadlines, and defaults.
/// </summary>
/// <typeparam name="TResult">The result type produced by select cases.</typeparam>
public sealed class SelectBuilder<TResult>
{
    private sealed record SelectCaseRegistration(int Priority, Func<TaskCompletionSource<Result<TResult>>, ChannelCase> Factory);

    private readonly List<SelectCaseRegistration> _caseFactories = [];
    private Func<TaskCompletionSource<Result<TResult>>, ChannelCase>? _defaultFactory;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider? _provider;
    private readonly CancellationToken _cancellationToken;

    internal SelectBuilder(TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
        _provider = provider;
        _cancellationToken = cancellationToken;
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        AddCase(reader, onValue, priority: 0);

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        AddCase(reader, onValue, priority);

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, (value, _) => onValue(value));
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, Task<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, (value, _) => onValue(value));
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, TResult> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, (value, _) => Task.FromResult(Result.Ok(onValue(value))));
    }

    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, TResult> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, (value, _) => Task.FromResult(Result.Ok(onValue(value))));
    }

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, priority, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, priority, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<TResult>> onValue) =>
        Case(template.Reader, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, CancellationToken, Task<TResult>> onValue) =>
        Case(template.Reader, priority, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<TResult>> onValue) =>
        Case(template.Reader, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, Task<TResult>> onValue) =>
        Case(template.Reader, priority, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, TResult> onValue) =>
        Case(template.Reader, onValue);

    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, TResult> onValue) =>
        Case(template.Reader, priority, onValue);

    public SelectBuilder<TResult> Default(Func<CancellationToken, Task<Result<TResult>>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        EnsureDefaultNotConfigured();
        _defaultFactory = completion => CreateDefaultCase(onDefault, completion, priority: 0);
        return this;
    }

    public SelectBuilder<TResult> Default(Func<Task<Result<TResult>>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(_ => onDefault());
    }

    public SelectBuilder<TResult> Default(Func<CancellationToken, Task<TResult>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(async (ct) => Result.Ok(await onDefault(ct).ConfigureAwait(false)));
    }

    public SelectBuilder<TResult> Default(Func<Task<TResult>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(async _ => Result.Ok(await onDefault().ConfigureAwait(false)));
    }

    public SelectBuilder<TResult> Default(Func<TResult> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(_ => Task.FromResult(Result.Ok(onDefault())));
    }

    public SelectBuilder<TResult> Default(TResult value) =>
        Default(() => value);

    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<CancellationToken, Task<Result<TResult>>> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        if (dueIn < TimeSpan.Zero || dueIn == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(dueIn));
        }

        var effectiveProvider = provider ?? _provider ?? TimeProvider.System;
        var reader = Go.After(dueIn, effectiveProvider, _cancellationToken);
        return AddCase(reader, async (_, ct) => await onDeadline(ct).ConfigureAwait(false), priority);
    }

    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<Task<Result<TResult>>> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, _ => onDeadline(), priority, provider);
    }

    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<CancellationToken, Task<TResult>> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, async ct => Result.Ok(await onDeadline(ct).ConfigureAwait(false)), priority, provider);
    }

    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<Task<TResult>> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, async _ => Result.Ok(await onDeadline().ConfigureAwait(false)), priority, provider);
    }

    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<TResult> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, _ => Task.FromResult(Result.Ok(onDeadline())), priority, provider);
    }

    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, TResult value, int priority = 0, TimeProvider? provider = null) =>
        Deadline(dueIn, () => value, priority, provider);

    private SelectBuilder<TResult> AddCase<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue, int priority)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onValue);

        _caseFactories.Add(new SelectCaseRegistration(priority, completion => CreateCase(reader, onValue, completion, priority)));
        return this;
    }

    private void EnsureDefaultNotConfigured()
    {
        if (_defaultFactory is not null)
        {
            throw new InvalidOperationException("A default case has already been configured for this select builder.");
        }
    }

    public async Task<Result<TResult>> ExecuteAsync()
    {
        if (_caseFactories.Count == 0 && _defaultFactory is null)
        {
            throw new InvalidOperationException("At least one channel case or a default case must be configured before executing the select.");
        }

        var completion = new TaskCompletionSource<Result<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cases = new ChannelCase[_caseFactories.Count];
        for (var i = 0; i < _caseFactories.Count; i++)
        {
            cases[i] = _caseFactories[i].Factory(completion);
        }

        ChannelCase? defaultCase = _defaultFactory is null ? null : _defaultFactory(completion);

        var selectResult = await Go.SelectInternalAsync(cases, defaultCase, _timeout, _provider, _cancellationToken).ConfigureAwait(false);

        if (completion.Task.IsCompleted)
        {
            return await completion.Task.ConfigureAwait(false);
        }

        if (selectResult.IsFailure)
        {
            return Result.Fail<TResult>(selectResult.Error ?? Error.Unspecified());
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private ChannelCase CreateCase<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue, TaskCompletionSource<Result<TResult>> completion, int priority)
    {
        var channelCase = ChannelCase.Create(reader, async (value, ct) =>
        {
            Result<TResult> caseResult;
            try
            {
                caseResult = await onValue(value, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                caseResult = Result.Fail<TResult>(Error.FromException(ex));
            }

            completion.TrySetResult(caseResult);

            return caseResult.IsSuccess
                ? Result.Ok(Go.Unit.Value)
                : Result.Fail<Go.Unit>(caseResult.Error ?? Error.Unspecified());
        });

        return channelCase.WithPriority(priority);
    }

    private ChannelCase CreateDefaultCase(Func<CancellationToken, Task<Result<TResult>>> onDefault, TaskCompletionSource<Result<TResult>> completion, int priority)
    {
        var defaultCase = ChannelCase.CreateDefault(async ct =>
        {
            Result<TResult> defaultResult;
            try
            {
                defaultResult = await onDefault(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                defaultResult = Result.Fail<TResult>(Error.FromException(ex));
            }

            completion.TrySetResult(defaultResult);

            return defaultResult.IsSuccess
                ? Result.Ok(Go.Unit.Value)
                : Result.Fail<Go.Unit>(defaultResult.Error ?? Error.Unspecified());
        }, priority);

        return defaultCase;
    }
}
