using Hugo.Policies;

namespace Hugo.Sagas;

/// <summary>
/// Provides saga-specific helpers on top of the pipeline step context.
/// </summary>
public sealed class ResultSagaStepContext
{
    private readonly ResultPipelineStepContext _pipelineContext;
    private readonly ResultSagaState _state;

    internal ResultSagaStepContext(ResultPipelineStepContext pipelineContext, ResultSagaState state)
    {
        _pipelineContext = pipelineContext ?? throw new ArgumentNullException(nameof(pipelineContext));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Gets the human-readable name of the current saga step.</summary>
    public string StepName => _pipelineContext.StepName;

    /// <summary>Gets the time provider associated with the saga execution.</summary>
    public TimeProvider TimeProvider => _pipelineContext.TimeProvider;

    /// <summary>Gets the shared saga state available to the step.</summary>
    public ResultSagaState State => _state;

    /// <summary>Registers a compensation action that runs if the saga rolls back.</summary>
    /// <param name="compensation">The compensating action to queue.</param>
    public void RegisterCompensation(Func<CancellationToken, ValueTask> compensation) => _pipelineContext.RegisterCompensation(compensation);

    /// <summary>Registers a stateful compensation action that runs if the saga rolls back.</summary>
    /// <typeparam name="TState">The type of state captured for compensation.</typeparam>
    /// <param name="state">The state passed to the compensation callback.</param>
    /// <param name="compensation">The compensating action to queue.</param>
    public void RegisterCompensation<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation) => _pipelineContext.RegisterCompensation(state, compensation);

    /// <summary>Conditionally registers a stateful compensation action.</summary>
    /// <typeparam name="TState">The type of state captured for compensation.</typeparam>
    /// <param name="state">The state passed to the compensation callback.</param>
    /// <param name="compensation">The compensating action to queue.</param>
    /// <param name="condition">A flag that determines whether the compensation is registered.</param>
    /// <returns><see langword="true"/> if the compensation was registered; otherwise <see langword="false"/>.</returns>
    public bool TryRegisterCompensation<TState>(TState state, Func<TState, CancellationToken, ValueTask> compensation, bool condition) => _pipelineContext.TryRegisterCompensation(state, compensation, condition);
}
