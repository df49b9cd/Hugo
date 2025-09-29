namespace Hugo;

/// <summary>
/// Provides functional-style extension methods for the Hugo-like result tuple `(T Value, Error? Err)`.
/// This enables a "Railway Oriented Programming" approach to error handling.
/// </summary>
public static class Functional
{
    // --- Synchronous Extensions ---

    /// <summary>
    /// Chains an operation that executes only if the previous result was successful.
    /// </summary>
    public static (TOut Value, Error? Err) Then<TIn, TOut>(
        this (TIn Value, Error? Err) result,
        Func<TIn, (TOut, Error?)> next
    ) => result.Err != null ? Go.Err<TOut>(result.Err) : next(result.Value);

    /// <summary>
    /// Transforms a successful result's value. If the result was a failure, the mapping function is skipped.
    /// </summary>
    public static (TOut Value, Error? Err) Map<TIn, TOut>(
        this (TIn Value, Error? Err) result,
        Func<TIn, TOut> mapper
    ) => result.Err != null ? Go.Err<TOut>(result.Err) : Go.Ok(mapper(result.Value));

    /// <summary>
    /// Performs a side effect action on a successful result without changing it. The action is skipped on failure.
    /// </summary>
    public static (T Value, Error? Err) Tee<T>(this (T Value, Error? Err) result, Action<T> action)
    {
        if (result.Err == null)
        {
            action(result.Value);
        }
        return result;
    }

    /// <summary>
    /// Extracts a final value from the result by providing functions to handle both success and failure cases.
    /// </summary>
    public static TOut Finally<TIn, TOut>(
        this (TIn Value, Error? Err) result,
        Func<TIn, TOut> onSuccess,
        Func<Error, TOut> onError
    ) => result.Err != null ? onError(result.Err) : onSuccess(result.Value);

    /// <summary>
    /// Recovers from a failure by executing a function that can optionally return a new success value.
    /// </summary>
    public static (T Value, Error? Err) Recover<T>(
        this (T Value, Error? Err) result,
        Func<Error, (T, Error?)> recoverFunc
    ) => result.Err != null ? recoverFunc(result.Err) : result;

    // --- Asynchronous Extensions ---

    /// <summary>
    /// Asynchronously chains an operation that executes only if the previous result was successful.
    /// </summary>
    public static async Task<(TOut Value, Error? Err)> ThenAsync<TIn, TOut>(
        this Task<(TIn Value, Error? Err)> resultTask,
        Func<TIn, (TOut, Error?)> next,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await resultTask;
            cancellationToken.ThrowIfCancellationRequested();
            return result.Err != null ? Go.Err<TOut>(result.Err) : next(result.Value);
        }
        catch (OperationCanceledException)
        {
            return Go.Err<TOut>(Go.CancellationError);
        }
    }

    /// <summary>
    /// Asynchronously chains an operation that executes only if the previous result was successful.
    /// </summary>
    public static async Task<(TOut Value, Error? Err)> ThenAsync<TIn, TOut>(
        this (TIn Value, Error? Err) result,
        Func<TIn, CancellationToken, Task<(TOut, Error?)>> nextAsync,
        CancellationToken cancellationToken = default
    )
    {
        if (result.Err != null)
            return Go.Err<TOut>(result.Err);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await nextAsync(result.Value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Go.Err<TOut>(Go.CancellationError);
        }
    }

    /// <summary>
    /// Asynchronously chains an operation that executes only if the previous result was successful.
    /// </summary>
    public static async Task<(TOut Value, Error? Err)> ThenAsync<TIn, TOut>(
        this Task<(TIn Value, Error? Err)> resultTask,
        Func<TIn, CancellationToken, Task<(TOut, Error?)>> nextAsync,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await resultTask;
            if (result.Err != null)
                return Go.Err<TOut>(result.Err);
            cancellationToken.ThrowIfCancellationRequested();
            return await nextAsync(result.Value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Go.Err<TOut>(Go.CancellationError);
        }
    }

    /// <summary>
    /// Asynchronously transforms a successful result's value. The mapping function is skipped on failure.
    /// </summary>
    public static async Task<(TOut Value, Error? Err)> MapAsync<TIn, TOut>(
        this Task<(TIn Value, Error? Err)> resultTask,
        Func<TIn, TOut> mapper,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await resultTask;
            cancellationToken.ThrowIfCancellationRequested();
            return result.Err != null ? Go.Err<TOut>(result.Err) : Go.Ok(mapper(result.Value));
        }
        catch (OperationCanceledException)
        {
            return Go.Err<TOut>(Go.CancellationError);
        }
    }

    /// <summary>
    /// Asynchronously performs a side-effect action on a successful result. The action is skipped on failure.
    /// </summary>
    public static async Task<(T Value, Error? Err)> TeeAsync<T>(
        this Task<(T Value, Error? Err)> resultTask,
        Action<T> action,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await resultTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Err == null)
            {
                action(result.Value);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return Go.Err<T>(Go.CancellationError);
        }
    }

    /// <summary>
    /// Asynchronously extracts a final value from the result by providing functions to handle both success and failure cases.
    /// </summary>
    public static async Task<TOut> FinallyAsync<TIn, TOut>(
        this Task<(TIn Value, Error? Err)> resultTask,
        Func<TIn, TOut> onSuccess,
        Func<Error, TOut> onError,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await resultTask;
            cancellationToken.ThrowIfCancellationRequested();
            return result.Err != null ? onError(result.Err) : onSuccess(result.Value);
        }
        catch (OperationCanceledException)
        {
            return onError(Go.CancellationError);
        }
    }

    /// <summary>
    /// Asynchronously recovers from a failure by executing a function that can optionally return a new success value.
    /// </summary>
    public static async Task<(T Value, Error? Err)> RecoverAsync<T>(
        this Task<(T Value, Error? Err)> resultTask,
        Func<Error, (T, Error?)> recoverFunc,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await resultTask;
            cancellationToken.ThrowIfCancellationRequested();
            return result.Err != null ? recoverFunc(result.Err) : result;
        }
        catch (OperationCanceledException)
        {
            return Go.Err<T>(Go.CancellationError);
        }
    }
}
