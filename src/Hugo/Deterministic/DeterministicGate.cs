namespace Hugo;

/// <summary>
/// Coordinates version decisions with deterministic side-effect capture.
/// </summary>
public sealed class DeterministicGate(VersionGate versionGate, DeterministicEffectStore effectStore)
{
    private readonly VersionGate _versionGate = versionGate ?? throw new ArgumentNullException(nameof(versionGate));
    private readonly DeterministicEffectStore _effectStore = effectStore ?? throw new ArgumentNullException(nameof(effectStore));

    /// <summary>
    /// Executes the upgraded or legacy branch for the supplied change identifier based on the resolved version gate decision.
    /// Captures the outcome deterministically so subsequent executions replay the same result.
    /// </summary>
    /// <typeparam name="T">The result type produced by the workflow branch.</typeparam>
    /// <param name="changeId">The unique identifier for the gated change.</param>
    /// <param name="minVersion">The minimum supported version for the change.</param>
    /// <param name="maxVersion">The maximum supported version for the change.</param>
    /// <param name="upgraded">Delegate invoked when the resolved version meets or exceeds <paramref name="maxVersion"/>.</param>
    /// <param name="legacy">Delegate invoked when the resolved version falls below <paramref name="maxVersion"/>.</param>
    /// <param name="initialVersionProvider">
    /// Optional factory used to seed the gate when no existing version marker is recorded. Defaults to <paramref name="maxVersion"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
    /// <returns>A <see cref="Result{T}"/> describing the replay-safe branch outcome.</returns>
    /// <remarks>
    /// The selected branch executes at most once for a given change identifier and version. Subsequent invocations replay the
    /// persisted effect captured through <see cref="DeterministicEffectStore"/>.
    /// </remarks>
    public Task<Result<T>> ExecuteAsync<T>(
        string changeId,
        int minVersion,
        int maxVersion,
        Func<CancellationToken, Task<Result<T>>> upgraded,
        Func<CancellationToken, Task<Result<T>>> legacy,
        Func<VersionGateContext, int>? initialVersionProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upgraded);
        ArgumentNullException.ThrowIfNull(legacy);

        return ExecuteAsync(
            changeId,
            minVersion,
            maxVersion,
            (decision, ct) => decision.Version >= maxVersion ? upgraded(ct) : legacy(ct),
            initialVersionProvider,
            cancellationToken);
    }

    /// <summary>
    /// Executes the supplied workflow delegate for the resolved version gate decision and stores the outcome deterministically.
    /// </summary>
    /// <typeparam name="T">The result type produced by the workflow delegate.</typeparam>
    /// <param name="changeId">The unique identifier for the gated change.</param>
    /// <param name="minVersion">The minimum supported version for the change.</param>
    /// <param name="maxVersion">The maximum supported version for the change.</param>
    /// <param name="onExecute">Delegate that executes the workflow for the resolved version decision.</param>
    /// <param name="initialVersionProvider">
    /// Optional factory used to seed the gate when no existing version marker is recorded. Defaults to <paramref name="maxVersion"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
    /// <returns>A <see cref="Result{T}"/> describing the replay-safe workflow outcome.</returns>
    public async Task<Result<T>> ExecuteAsync<T>(
        string changeId,
        int minVersion,
        int maxVersion,
        Func<VersionDecision, CancellationToken, Task<Result<T>>> onExecute,
        Func<VersionGateContext, int>? initialVersionProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeId);
        ArgumentNullException.ThrowIfNull(onExecute);

        var decisionResult = _versionGate.Require(changeId, minVersion, maxVersion, initialVersionProvider);
        if (decisionResult.IsFailure)
        {
            return Result.Fail<T>(decisionResult.Error ?? Error.Unspecified());
        }

        var decision = decisionResult.Value;
        var effectId = BuildEffectId(changeId, decision.Version);

        return await _effectStore.CaptureAsync(
            effectId,
            ct => onExecute(decision, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a fluent builder that configures deterministic workflow branches for the specified change identifier.
    /// </summary>
    /// <typeparam name="TResult">The result type produced by the configured workflow.</typeparam>
    /// <param name="changeId">The unique identifier for the gated change.</param>
    /// <param name="minVersion">The minimum supported version for the change.</param>
    /// <param name="maxVersion">The maximum supported version for the change.</param>
    /// <param name="initialVersionProvider">
    /// Optional factory used to seed the gate when no existing version marker is recorded. Defaults to <paramref name="maxVersion"/>.
    /// </param>
    /// <returns>A builder that can register version-specific branches and execute them deterministically.</returns>
    public DeterministicWorkflowBuilder<TResult> Workflow<TResult>(
        string changeId,
        int minVersion,
        int maxVersion,
        Func<VersionGateContext, int>? initialVersionProvider = null) =>
        new(this, changeId, minVersion, maxVersion, initialVersionProvider);

    /// <summary>
    /// Builds a deterministic effect identifier that incorporates the change identifier, version, and optional scope.
    /// </summary>
    /// <param name="changeId">The unique change identifier.</param>
    /// <param name="version">The resolved version.</param>
    /// <param name="scope">Optional workflow scope or step identifier.</param>
    /// <returns>A normalized effect identifier used for deterministic storage.</returns>
    private static string BuildEffectId(string changeId, int version, string? scope = null)
    {
        var baseId = $"{changeId}::v{version}";
        if (string.IsNullOrWhiteSpace(scope))
        {
            return baseId;
        }

        return $"{baseId}::{scope}";
    }

    /// <summary>
    /// Normalizes the provided scope string to ensure consistent deterministic identifiers.
    /// </summary>
    /// <param name="scope">The user-supplied scope identifier.</param>
    /// <returns>A trimmed, non-empty scope value.</returns>
    private static string NormalizeScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Step identifier must be provided.", nameof(scope));
        }

        return scope.Trim();
    }

    /// <summary>
    /// Provides a fluent API for registering version-specific workflow branches that execute deterministically.
    /// </summary>
    /// <typeparam name="TResult">The result type produced by the workflow.</typeparam>
    public sealed class DeterministicWorkflowBuilder<TResult>
    {
        private readonly DeterministicGate _gate;
        private readonly string _changeId;
        private readonly int _minVersion;
        private readonly int _maxVersion;
        private readonly Func<VersionGateContext, int>? _initialVersionProvider;
        private readonly List<WorkflowBranch<TResult>> _branches = [];
        private Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>>? _fallback;

        internal DeterministicWorkflowBuilder(
            DeterministicGate gate,
            string changeId,
            int minVersion,
            int maxVersion,
            Func<VersionGateContext, int>? initialVersionProvider)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            ArgumentException.ThrowIfNullOrWhiteSpace(changeId);

            if (minVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minVersion), minVersion, "Minimum version must be greater than zero.");
            }

            if (maxVersion < minVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(maxVersion), maxVersion, "Maximum version must be greater than or equal to the minimum version.");
            }

            _changeId = changeId.Trim();
            _minVersion = minVersion;
            _maxVersion = maxVersion;
            _initialVersionProvider = initialVersionProvider;
        }

        /// <summary>
        /// Registers a branch that executes when the resolved version exactly matches <paramref name="version"/>.
        /// </summary>
        /// <param name="version">The specific version to match.</param>
        /// <param name="executor">Delegate that produces the branch result.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> ForVersion(
            int version,
            Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> executor)
        {
            if (version < _minVersion || version > _maxVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version, "Version must be within the configured bounds.");
            }

            return For(decision => decision.Version == version, executor);
        }

        /// <summary>
        /// Registers a synchronous branch that executes when the resolved version exactly matches <paramref name="version"/>.
        /// </summary>
        /// <param name="version">The specific version to match.</param>
        /// <param name="executor">Delegate that produces the branch result synchronously.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> ForVersion(
            int version,
            Func<DeterministicWorkflowContext, Result<TResult>> executor) =>
            ForVersion(version, (context, _) => Task.FromResult(executor(context)));

        /// <summary>
        /// Registers a branch that executes when the resolved version falls within the supplied inclusive range.
        /// </summary>
        /// <param name="minVersion">The minimum version to match.</param>
        /// <param name="maxVersion">The maximum version to match.</param>
        /// <param name="executor">Delegate that produces the branch result.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> ForRange(
            int minVersion,
            int maxVersion,
            Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> executor)
        {
            if (minVersion < _minVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(minVersion), minVersion, "Range minimum must be within the configured bounds.");
            }

            if (maxVersion > _maxVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(maxVersion), maxVersion, "Range maximum must be within the configured bounds.");
            }

            if (maxVersion < minVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(maxVersion), maxVersion, "Range maximum must be greater than or equal to the range minimum.");
            }

            return For(decision => decision.Version >= minVersion && decision.Version <= maxVersion, executor);
        }

        /// <summary>
        /// Registers a synchronous branch that executes when the resolved version falls within the supplied inclusive range.
        /// </summary>
        /// <param name="minVersion">The minimum version to match.</param>
        /// <param name="maxVersion">The maximum version to match.</param>
        /// <param name="executor">Delegate that produces the branch result synchronously.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> ForRange(
            int minVersion,
            int maxVersion,
            Func<DeterministicWorkflowContext, Result<TResult>> executor) =>
            ForRange(minVersion, maxVersion, (context, _) => Task.FromResult(executor(context)));

        /// <summary>
        /// Registers a branch that executes when the supplied predicate evaluates to <c>true</c> for the resolved decision.
        /// </summary>
        /// <param name="predicate">Predicate used to match the decision.</param>
        /// <param name="executor">Delegate that produces the branch result.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> For(
            Func<VersionDecision, bool> predicate,
            Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> executor)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(executor);

            _branches.Add(new WorkflowBranch<TResult>(predicate, executor));
            return this;
        }

        /// <summary>
        /// Registers a synchronous branch that executes when the supplied predicate evaluates to <c>true</c> for the resolved decision.
        /// </summary>
        /// <param name="predicate">Predicate used to match the decision.</param>
        /// <param name="executor">Delegate that produces the branch result synchronously.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> For(
            Func<VersionDecision, bool> predicate,
            Func<DeterministicWorkflowContext, Result<TResult>> executor) =>
            For(predicate, (context, _) => Task.FromResult(executor(context)));

        /// <summary>
        /// Registers a fallback branch that executes when no other branch predicates match the resolved decision.
        /// </summary>
        /// <param name="executor">Delegate invoked when no branch matches.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> WithFallback(
            Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> executor)
        {
            ArgumentNullException.ThrowIfNull(executor);
            _fallback = executor;
            return this;
        }

        /// <summary>
        /// Registers a synchronous fallback branch that executes when no other branch predicates match the resolved decision.
        /// </summary>
        /// <param name="executor">Delegate invoked when no branch matches.</param>
        /// <returns>The current builder instance to support fluent chaining.</returns>
        public DeterministicWorkflowBuilder<TResult> WithFallback(
            Func<DeterministicWorkflowContext, Result<TResult>> executor)
        {
            ArgumentNullException.ThrowIfNull(executor);
            return WithFallback((context, _) => Task.FromResult(executor(context)));
        }

        /// <summary>
        /// Executes the deterministic workflow by evaluating registered branches and capturing side-effects.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
        /// <returns>A <see cref="Result{TResult}"/> describing the replay-safe workflow outcome.</returns>
        public Task<Result<TResult>> ExecuteAsync(CancellationToken cancellationToken = default) =>
            _gate.ExecuteAsync(
                _changeId,
                _minVersion,
                _maxVersion,
                ExecuteBranchAsync,
                _initialVersionProvider,
                cancellationToken);

        /// <summary>
        /// Resolves the matching branch and executes it within the deterministic context.
        /// </summary>
        /// <param name="decision">The resolved version decision.</param>
        /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
        /// <returns>A <see cref="Result{TResult}"/> describing the branch outcome.</returns>
        private async Task<Result<TResult>> ExecuteBranchAsync(VersionDecision decision, CancellationToken cancellationToken)
        {
            var executor = Resolve(decision);
            if (executor is null)
            {
                var error = Error.From(
                        $"No workflow branch was registered for version {decision.Version}.",
                        ErrorCodes.VersionConflict)
                    .WithMetadata(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["changeId"] = _changeId,
                        ["version"] = decision.Version,
                        ["minVersion"] = _minVersion,
                        ["maxVersion"] = _maxVersion
                    });

                return Result.Fail<TResult>(error);
            }

            var context = new DeterministicWorkflowContext(_gate, _changeId, decision);

            try
            {
                return await executor(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<TResult>(Error.Canceled(token: oce.CancellationToken));
            }
            catch (Exception ex)
            {
                return Result.Fail<TResult>(Error.FromException(ex));
            }
        }

        /// <summary>
        /// Finds the first registered branch whose predicate matches the supplied decision.
        /// </summary>
        /// <param name="decision">The resolved version decision.</param>
        /// <returns>The registered executor, or <c>null</c> when no branch matches.</returns>
        private Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>>? Resolve(VersionDecision decision)
        {
            foreach (var branch in _branches)
            {
                if (branch.Predicate(decision))
                {
                    return branch.Executor;
                }
            }

            return _fallback;
        }
    }

    /// <summary>
    /// Represents the deterministic execution context surfaced to workflow branches and effects.
    /// </summary>
    public sealed class DeterministicWorkflowContext
    {
        private readonly DeterministicGate _gate;
        private readonly string _changeId;

        internal DeterministicWorkflowContext(DeterministicGate gate, string changeId, VersionDecision decision)
        {
            _gate = gate;
            _changeId = changeId;
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        }

        /// <summary>
        /// Gets the resolved version decision.
        /// </summary>
        public VersionDecision Decision { get; }

        /// <summary>
        /// Gets the change identifier associated with the deterministic workflow.
        /// </summary>
        public string ChangeId => _changeId;

        /// <summary>
        /// Gets the resolved version number.
        /// </summary>
        public int Version => Decision.Version;

        /// <summary>
        /// Gets a value indicating whether the version decision was newly recorded.
        /// </summary>
        public bool IsNew => Decision.IsNew;

        /// <summary>
        /// Gets the timestamp associated with the version decision.
        /// </summary>
        public DateTimeOffset RecordedAt => Decision.RecordedAt;

        /// <summary>
        /// Captures the supplied asynchronous side-effect within the deterministic effect store.
        /// </summary>
        /// <typeparam name="T">The side-effect result type.</typeparam>
        /// <param name="stepId">Unique identifier for the step within the workflow.</param>
        /// <param name="effect">Delegate that performs the side-effect.</param>
        /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
        /// <returns>A <see cref="Result{T}"/> describing the deterministic effect outcome.</returns>
        public Task<Result<T>> CaptureAsync<T>(
            string stepId,
            Func<CancellationToken, Task<Result<T>>> effect,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return _gate._effectStore.CaptureAsync(
                CreateEffectId(stepId),
                effect,
                cancellationToken);
        }

        /// <summary>
        /// Captures the supplied asynchronous side-effect within the deterministic effect store.
        /// </summary>
        /// <typeparam name="T">The side-effect result type.</typeparam>
        /// <param name="stepId">Unique identifier for the step within the workflow.</param>
        /// <param name="effect">Delegate that performs the side-effect.</param>
        /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
        /// <returns>A <see cref="Result{T}"/> describing the deterministic effect outcome.</returns>
        public Task<Result<T>> CaptureAsync<T>(
            string stepId,
            Func<Task<Result<T>>> effect,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return CaptureAsync(stepId, _ => effect(), cancellationToken);
        }

        /// <summary>
        /// Captures the supplied synchronous side-effect within the deterministic effect store.
        /// </summary>
        /// <typeparam name="T">The side-effect result type.</typeparam>
        /// <param name="stepId">Unique identifier for the step within the workflow.</param>
        /// <param name="effect">Delegate that performs the side-effect.</param>
        /// <param name="cancellationToken">Token used to cancel the deterministic execution flow.</param>
        /// <returns>A <see cref="Result{T}"/> describing the deterministic effect outcome.</returns>
        public Task<Result<T>> CaptureAsync<T>(
            string stepId,
            Func<Result<T>> effect,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return CaptureAsync(stepId, _ => Task.FromResult(effect()), cancellationToken);
        }

        /// <summary>
        /// Composes a deterministic effect identifier for the supplied step.
        /// </summary>
        /// <param name="stepId">The workflow step identifier.</param>
        /// <returns>An effect identifier suitable for deterministic storage.</returns>
        public string CreateEffectId(string stepId)
        {
            var normalized = NormalizeScope(stepId);
            return BuildEffectId(_changeId, Version, normalized);
        }
    }

    /// <summary>
    /// Represents a registered deterministic workflow branch.
    /// </summary>
    /// <typeparam name="TResult">The branch result type.</typeparam>
    private sealed record WorkflowBranch<TResult>(
        Func<VersionDecision, bool> Predicate,
        Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> Executor);
}
