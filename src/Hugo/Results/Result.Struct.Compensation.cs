using System;
using System.Threading;

using Hugo.Policies;

namespace Hugo;

public readonly partial record struct Result<T>
{
    /// <summary>
    /// Attaches a compensation callback to the result so orchestrators can absorb the action if the pipeline fails later.
    /// </summary>
    /// <param name="compensation">The callback to execute when compensation runs.</param>
    /// <returns>A new <see cref="Result{T}"/> that carries the provided compensation.</returns>
    public Result<T> WithCompensation(Func<CancellationToken, ValueTask> compensation)
    {
        ArgumentNullException.ThrowIfNull(compensation);

        var scope = _compensation ?? new CompensationScope();
        scope.Register(compensation);
        return CloneWith(scope);
    }

    /// <summary>
    /// Attaches a stateful compensation callback to the result so orchestrators can absorb the action if the pipeline fails later.
    /// </summary>
    /// <typeparam name="TState">The type of state captured by the compensation.</typeparam>
    /// <param name="state">The captured state.</param>
    /// <param name="compensation">The callback to execute when compensation runs.</param>
    /// <returns>A new <see cref="Result{T}"/> that carries the provided compensation.</returns>
    public Result<T> WithCompensation<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation)
    {
        ArgumentNullException.ThrowIfNull(compensation);

        var scope = _compensation ?? new CompensationScope();
        scope.Register(state, compensation);
        return CloneWith(scope);
    }

    internal Result<T> CloneWith(CompensationScope? scope) => new(_value, Error, IsSuccess, scope);
}
