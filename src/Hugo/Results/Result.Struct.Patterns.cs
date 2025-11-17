namespace Hugo;

public readonly partial record struct Result<T>
{
    /// <summary>Executes the appropriate callback depending on success or failure.</summary>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    public void Switch(Action<T> onSuccess, Action<Error> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        if (IsSuccess)
        {
            onSuccess(_value);
        }
        else
        {
            onFailure(Error!);
        }
    }

    /// <summary>Projects the result onto a new value using the provided callbacks.</summary>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    /// <returns>The projected value.</returns>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        return IsSuccess ? onSuccess(_value) : onFailure(Error!);
    }

    /// <summary>Executes asynchronous callbacks depending on success or failure.</summary>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    /// <param name="cancellationToken">The token used to cancel callback execution.</param>
    /// <returns>A task representing the asynchronous callbacks.</returns>
    public ValueTask SwitchAsync(Func<T, CancellationToken, ValueTask> onSuccess, Func<Error, CancellationToken, ValueTask> onFailure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        cancellationToken.ThrowIfCancellationRequested();
        return IsSuccess
            ? onSuccess(_value, cancellationToken)
            : onFailure(Error!, cancellationToken);
    }

    /// <summary>Projects the result asynchronously onto a new value.</summary>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    /// <param name="cancellationToken">The token used to cancel callback execution.</param>
    /// <returns>The projected value.</returns>
    public ValueTask<TResult> MatchAsync<TResult>(Func<T, CancellationToken, ValueTask<TResult>> onSuccess, Func<Error, CancellationToken, ValueTask<TResult>> onFailure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        cancellationToken.ThrowIfCancellationRequested();
        return IsSuccess
            ? onSuccess(_value, cancellationToken)
            : onFailure(Error!, cancellationToken);
    }
}
