using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Fluent builder for typed channel select operations with priorities, deadlines, and defaults.
/// </summary>
/// <typeparam name="TResult">The result type produced by select cases.</typeparam>
public sealed class SelectBuilder<TResult>
{
    private sealed record SelectCaseRegistration(int Priority, Func<TaskCompletionSource<Result<TResult>>, ChannelCase<TResult>> Factory);

    private readonly List<SelectCaseRegistration> _caseFactories = [];
    private Func<TaskCompletionSource<Result<TResult>>, ChannelCase<TResult>>? _defaultFactory;
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

    /// <summary>
    /// Registers a channel reader case that processes each value with an asynchronous handler capable of observing cancellation.
    /// </summary>
    /// <typeparam name="T">The type of values produced by <paramref name="reader"/>.</typeparam>
    /// <param name="reader">The channel to monitor for incoming values.</param>
    /// <param name="onValue">Handler invoked when a value is read. Receives the value and the select cancellation token and must return a <see cref="Result{TResult}"/>.</param>
    /// <returns>The current <see cref="SelectBuilder{TResult}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> or <paramref name="onValue"/> is <c>null</c>.</exception>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        AddCase(reader, onValue, priority: 0);

    public SelectBuilder<TResult> CaseValueTask<T>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Case(reader, (value, token) => onValue(value, token).AsTask());
    }


    /// <summary>
    /// Registers a channel reader case with an explicit priority. Lower values indicate higher priority when multiple cases are ready.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    /// <param name="priority">The relative priority used to break ties between ready cases.</param>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        AddCase(reader, onValue, priority);

    public SelectBuilder<TResult> CaseValueTask<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Case(reader, priority, (value, token) => onValue(value, token).AsTask());
    }


    /// <summary>
    /// Registers a channel reader case whose handler does not need the cancellation token.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, (value, _) => onValue(value));
    }

    /// <summary>
    /// Registers a channel reader case with an explicit priority whose handler does not need the cancellation token.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, int, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, Task<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, (value, _) => onValue(value));
    }

    /// <summary>
    /// Registers a channel reader case whose handler produces a raw <typeparamref name="TResult"/> asynchronously and is aware of cancellation.
    /// </summary>
    /// <typeparam name="T">The type of values produced by <paramref name="reader"/>.</typeparam>
    /// <param name="reader">The channel to monitor for incoming values.</param>
    /// <param name="onValue">Handler invoked when a value is read. Receives the value and the select cancellation token and returns the projected result.</param>
    /// <returns>The current <see cref="SelectBuilder{TResult}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> or <paramref name="onValue"/> is <c>null</c>.</exception>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// Registers a cancellation-aware channel reader case with an explicit priority.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, Func{T, CancellationToken, Task{TResult}})"/>
    /// <param name="priority">The relative priority used to break ties between ready cases.</param>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// Registers a channel reader case whose handler produces a raw <typeparamref name="TResult"/> asynchronously without observing cancellation.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, Func{T, CancellationToken, Task{TResult}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
    }

    /// <summary>
    /// Registers a non-cancellation-aware channel reader case with an explicit priority.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, int, Func{T, CancellationToken, Task{TResult}})"/>
    /// <param name="priority">The relative priority used to break ties between ready cases.</param>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, Task<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
    }

    /// <summary>
    /// Registers a synchronous channel reader case that immediately projects values into <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="T">The type of values produced by <paramref name="reader"/>.</typeparam>
    /// <param name="reader">The channel to monitor for incoming values.</param>
    /// <param name="onValue">Synchronous projection invoked for each value.</param>
    /// <returns>The current builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> or <paramref name="onValue"/> is <c>null</c>.</exception>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, TResult> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, (value, _) => Task.FromResult(Result.Ok(onValue(value))));
    }

    /// <summary>
    /// Registers a synchronous channel reader case with an explicit priority.
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, Func{T, TResult})"/>
    /// <param name="priority">The relative priority used to break ties between ready cases.</param>
    public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, TResult> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return Case(reader, priority, (value, _) => Task.FromResult(Result.Ok(onValue(value))));
    }

    /// <summary>
    /// Registers a channel case using a previously captured <see cref="ChannelCaseTemplate{T}"/>.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelReader{T}, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    /// <param name="template">Template created from a reader that will be materialized for this select.</param>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, onValue);

    /// <summary>
    /// Registers a templated channel case with an explicit priority.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    /// <param name="priority">The relative priority used to break ties between ready cases.</param>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, priority, onValue);

    /// <summary>
    /// Registers a templated channel case whose handler does not observe cancellation.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, onValue);

    /// <summary>
    /// Registers a templated channel case with an explicit priority whose handler does not observe cancellation.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, int, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, Task<Result<TResult>>> onValue) =>
        Case(template.Reader, priority, onValue);

    /// <summary>
    /// Registers a templated channel case that projects values into <typeparamref name="TResult"/> asynchronously.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, Func{T, CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<TResult>> onValue) =>
        Case(template.Reader, onValue);

    /// <summary>
    /// Registers a templated channel case with an explicit priority that projects values into <typeparamref name="TResult"/> asynchronously.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, Func{T, CancellationToken, Task{TResult}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, CancellationToken, Task<TResult>> onValue) =>
        Case(template.Reader, priority, onValue);

    /// <summary>
    /// Registers a templated channel case whose handler ignores cancellation and returns <typeparamref name="TResult"/>.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, Func{T, CancellationToken, Task{TResult}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<TResult>> onValue) =>
        Case(template.Reader, onValue);

    /// <summary>
    /// Registers a templated channel case with an explicit priority whose handler ignores cancellation and returns <typeparamref name="TResult"/>.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, int, Func{T, CancellationToken, Task{TResult}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, Task<TResult>> onValue) =>
        Case(template.Reader, priority, onValue);

    /// <summary>
    /// Registers a templated channel case with a synchronous projection.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, Func{T, CancellationToken, Task{TResult}})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, TResult> onValue) =>
        Case(template.Reader, onValue);

    /// <summary>
    /// Registers a templated channel case with a synchronous projection and explicit priority.
    /// </summary>
    /// <inheritdoc cref="Case{T}(ChannelCaseTemplate{T}, Func{T, TResult})"/>
    public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, TResult> onValue) =>
        Case(template.Reader, priority, onValue);

    /// <summary>
    /// Configures a default case that runs when no channel case is immediately ready.
    /// </summary>
    /// <param name="onDefault">Handler invoked with the select cancellation token when the default branch is chosen.</param>
    /// <returns>The current builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="onDefault"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a default case has already been configured.</exception>
    public SelectBuilder<TResult> Default(Func<CancellationToken, Task<Result<TResult>>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        EnsureDefaultNotConfigured();
        _defaultFactory = completion => CreateDefaultCase(onDefault, completion, priority: 0);
        return this;
    }

    public SelectBuilder<TResult> DefaultValueTask(Func<CancellationToken, ValueTask<Result<TResult>>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);
        return Default(token => onDefault(token).AsTask());
    }


    /// <summary>
    /// Configures a default case whose handler does not observe cancellation.
    /// </summary>
    /// <inheritdoc cref="Default(Func<CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Default(Func<Task<Result<TResult>>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(_ => onDefault());
    }

    /// <summary>
    /// Configures a default case whose handler produces <typeparamref name="TResult"/> and is cancellation-aware.
    /// </summary>
    /// <inheritdoc cref="Default(Func<CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Default(Func<CancellationToken, Task<TResult>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(async (ct) => Result.Ok(await onDefault(ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// Configures a default case whose handler produces <typeparamref name="TResult"/> without observing cancellation.
    /// </summary>
    /// <inheritdoc cref="Default(Func<CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Default(Func<Task<TResult>> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(async _ => Result.Ok(await onDefault().ConfigureAwait(false)));
    }

    /// <summary>
    /// Configures a default case that returns a synchronous value.
    /// </summary>
    /// <inheritdoc cref="Default(Func<CancellationToken, Task{Result{TResult}}})"/>
    public SelectBuilder<TResult> Default(Func<TResult> onDefault)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return Default(_ => Task.FromResult(Result.Ok(onDefault())));
    }

    /// <summary>
    /// Configures a default case that always yields the provided <paramref name="value"/>.
    /// </summary>
    /// <inheritdoc cref="Default(Func{TResult})"/>
    public SelectBuilder<TResult> Default(TResult value) =>
        Default(() => value);

    /// <summary>
    /// Adds a deadline case that triggers after <paramref name="dueIn"/> using the specified time provider.
    /// </summary>
    /// <param name="dueIn">Amount of time to wait before the deadline fires. Must be greater than zero.</param>
    /// <param name="onDeadline">Handler invoked when the deadline elapses. Receives the select cancellation token.</param>
    /// <param name="priority">Optional priority applied when multiple cases are ready. Lower values win.</param>
    /// <param name="provider">Optional <see cref="TimeProvider"/> to use for scheduling the deadline. Defaults to the builder's provider or <see cref="TimeProvider.System"/>.</param>
    /// <returns>The current builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="onDeadline"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="dueIn"/> is negative or infinite.</exception>
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

    /// <summary>
    /// Adds a deadline case whose handler does not observe cancellation.
    /// </summary>
    /// <inheritdoc cref="Deadline(TimeSpan, Func{CancellationToken, Task{Result{TResult}}}, int, TimeProvider?)"/>
    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<Task<Result<TResult>>> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, _ => onDeadline(), priority, provider);
    }

    /// <summary>
    /// Adds a deadline case that produces <typeparamref name="TResult"/> asynchronously and observes cancellation.
    /// </summary>
    /// <inheritdoc cref="Deadline(TimeSpan, Func{CancellationToken, Task{Result{TResult}}}, int, TimeProvider?)"/>
    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<CancellationToken, Task<TResult>> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, async ct => Result.Ok(await onDeadline(ct).ConfigureAwait(false)), priority, provider);
    }

    /// <summary>
    /// Adds a deadline case that produces <typeparamref name="TResult"/> asynchronously without observing cancellation.
    /// </summary>
    /// <inheritdoc cref="Deadline(TimeSpan, Func{CancellationToken, Task{Result{TResult}}}, int, TimeProvider?)"/>
    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<Task<TResult>> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, async _ => Result.Ok(await onDeadline().ConfigureAwait(false)), priority, provider);
    }

    /// <summary>
    /// Adds a deadline case that synchronously returns <typeparamref name="TResult"/>.
    /// </summary>
    /// <inheritdoc cref="Deadline(TimeSpan, Func{CancellationToken, Task{Result{TResult}}}, int, TimeProvider?)"/>
    public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<TResult> onDeadline, int priority = 0, TimeProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(onDeadline);

        return Deadline(dueIn, _ => Task.FromResult(Result.Ok(onDeadline())), priority, provider);
    }

    /// <summary>
    /// Adds a deadline case that yields the provided <paramref name="value"/>.
    /// <inheritdoc cref="Deadline(TimeSpan, Func{CancellationToken, Task{Result{TResult}}}, int, TimeProvider?)"/>
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

    /// <summary>
    /// Materializes the registered channel cases, awaits the select, and returns the resulting value or error.
    /// </summary>
    public async ValueTask<Result<TResult>> ExecuteAsync()
    {
        if (_caseFactories.Count == 0 && _defaultFactory is null)
        {
            throw new InvalidOperationException("At least one channel case or a default case must be configured before executing the select.");
        }

        var completion = new TaskCompletionSource<Result<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cases = new ChannelCase<TResult>[_caseFactories.Count];
        for (var i = 0; i < _caseFactories.Count; i++)
        {
            cases[i] = _caseFactories[i].Factory(completion);
        }

        ChannelCase<TResult>? defaultCase = _defaultFactory?.Invoke(completion);

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

    private static ChannelCase<TResult> CreateCase<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue, TaskCompletionSource<Result<TResult>> completion, int priority)
    {
        ChannelCase<TResult> channelCase = ChannelCase.Create(reader, InvokeAsync);

        async ValueTask<Result<TResult>> InvokeAsync(T value, CancellationToken ct)
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

            return caseResult;
        }

        return channelCase.WithPriority(priority);
    }

    private static ChannelCase<TResult> CreateDefaultCase(Func<CancellationToken, Task<Result<TResult>>> onDefault, TaskCompletionSource<Result<TResult>> completion, int priority)
    {
        var defaultCase = ChannelCase.CreateDefault(WrapDefaultAsync, priority);

        async ValueTask<Result<TResult>> WrapDefaultAsync(CancellationToken ct)
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

            return defaultResult;
        }

        return defaultCase;
    }
}
