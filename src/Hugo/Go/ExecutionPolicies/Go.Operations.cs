using System.Diagnostics.CodeAnalysis;
using Hugo.Policies;

namespace Hugo;

/// <content>
/// Provides helpers for orchestrating concurrent operations with policy support.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Executes multiple operations concurrently using <see cref="Result.WhenAll{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, TimeProvider?, CancellationToken)"/> with an optional execution policy.
    /// </summary>
    /// <typeparam name="T">The result type produced by each operation.</typeparam>
    /// <param name="operations">The operations to execute concurrently.</param>
    /// <param name="policy">The optional execution policy applied to each operation.</param>
    /// <param name="cancellationToken">The token used to cancel the aggregate operation.</param>
    /// <param name="timeProvider">The optional time provider used by the execution policy.</param>
    /// <returns>A result containing the collection of operation outputs.</returns>
    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Preserves established public API ordering for downstream callers.")]
    public static Task<Result<IReadOnlyList<T>>> FanOutAsync<T>(
        IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => new ValueTask<Result<T>>(operation(ct))));

        return Result.WhenAll(adapted, policy, timeProvider, cancellationToken);
    }

    /// <summary>
    /// Executes multiple operations concurrently and returns the first successful result via <see cref="Result.WhenAny{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, TimeProvider?, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="T">The result type produced by each operation.</typeparam>
    /// <param name="operations">The operations to execute concurrently.</param>
    /// <param name="policy">The optional execution policy applied to each operation.</param>
    /// <param name="cancellationToken">The token used to cancel the aggregate operation.</param>
    /// <param name="timeProvider">The optional time provider used by the execution policy.</param>
    /// <returns>A result containing the first successful operation output.</returns>
    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Preserves established public API ordering for downstream callers.")]
    public static Task<Result<T>> RaceAsync<T>(
        IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => new ValueTask<Result<T>>(operation(ct))));

        return Result.WhenAny(adapted, policy, timeProvider, cancellationToken);
    }
}
