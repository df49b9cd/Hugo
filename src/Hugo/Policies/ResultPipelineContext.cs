namespace Hugo.Policies;

/// <summary>
/// Provides contextual helpers to pipeline steps executed under a <see cref="ResultExecutionPolicy"/>.
/// </summary>
public sealed class ResultPipelineStepContext
{
    private readonly CompensationScope _compensationScope;

    internal ResultPipelineStepContext(string? stepName, CompensationScope compensationScope, TimeProvider timeProvider)
    {
        StepName = string.IsNullOrWhiteSpace(stepName) ? string.Empty : stepName;
        _compensationScope = compensationScope ?? throw new ArgumentNullException(nameof(compensationScope));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Gets the name associated with the current step for diagnostics.
    /// </summary>
    public string StepName { get; }

    /// <summary>
    /// Provides access to the time provider used by the pipeline.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Registers a compensation action to be executed if the pipeline fails.
    /// </summary>
    public void RegisterCompensation(Func<CancellationToken, ValueTask> compensation) => _compensationScope.Register(compensation);

    /// <summary>
    /// Registers a compensation action that captures state.
    /// </summary>
    public void RegisterCompensation<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation) => _compensationScope.Register(state, compensation);

    /// <summary>
    /// Attempts to register a compensation action when the provided predicate returns true.
    /// </summary>
    public bool TryRegisterCompensation<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation, bool condition)
    {
        if (!condition)
        {
            return false;
        }

        RegisterCompensation(state, compensation);
        return true;
    }
}
