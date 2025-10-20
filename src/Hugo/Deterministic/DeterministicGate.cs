using System.Collections.Generic;

namespace Hugo;

/// <summary>
/// Coordinates version decisions with deterministic side-effect capture.
/// </summary>
public sealed class DeterministicGate
{
    private readonly VersionGate _versionGate;
    private readonly DeterministicEffectStore _effectStore;

    public DeterministicGate(VersionGate versionGate, DeterministicEffectStore effectStore)
    {
        _versionGate = versionGate ?? throw new ArgumentNullException(nameof(versionGate));
        _effectStore = effectStore ?? throw new ArgumentNullException(nameof(effectStore));
    }

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

    public DeterministicWorkflowBuilder<TResult> Workflow<TResult>(
        string changeId,
        int minVersion,
        int maxVersion,
        Func<VersionGateContext, int>? initialVersionProvider = null) =>
        new(this, changeId, minVersion, maxVersion, initialVersionProvider);

    private static string BuildEffectId(string changeId, int version, string? scope = null)
    {
        var baseId = $"{changeId}::v{version}";
        if (string.IsNullOrWhiteSpace(scope))
        {
            return baseId;
        }

        return $"{baseId}::{scope}";
    }

    private static string NormalizeScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Step identifier must be provided.", nameof(scope));
        }

        return scope.Trim();
    }

    public sealed class DeterministicWorkflowBuilder<TResult>
    {
        private readonly DeterministicGate _gate;
        private readonly string _changeId;
        private readonly int _minVersion;
        private readonly int _maxVersion;
        private readonly Func<VersionGateContext, int>? _initialVersionProvider;
        private readonly List<WorkflowBranch<TResult>> _branches = new();
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

        public DeterministicWorkflowBuilder<TResult> ForVersion(
            int version,
            Func<DeterministicWorkflowContext, Result<TResult>> executor) =>
            ForVersion(version, (context, _) => Task.FromResult(executor(context)));

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

        public DeterministicWorkflowBuilder<TResult> ForRange(
            int minVersion,
            int maxVersion,
            Func<DeterministicWorkflowContext, Result<TResult>> executor) =>
            ForRange(minVersion, maxVersion, (context, _) => Task.FromResult(executor(context)));

        public DeterministicWorkflowBuilder<TResult> For(
            Func<VersionDecision, bool> predicate,
            Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> executor)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(executor);

            _branches.Add(new WorkflowBranch<TResult>(predicate, executor));
            return this;
        }

        public DeterministicWorkflowBuilder<TResult> For(
            Func<VersionDecision, bool> predicate,
            Func<DeterministicWorkflowContext, Result<TResult>> executor) =>
            For(predicate, (context, _) => Task.FromResult(executor(context)));

        public DeterministicWorkflowBuilder<TResult> WithFallback(
            Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> executor)
        {
            ArgumentNullException.ThrowIfNull(executor);
            _fallback = executor;
            return this;
        }

        public DeterministicWorkflowBuilder<TResult> WithFallback(
            Func<DeterministicWorkflowContext, Result<TResult>> executor)
        {
            ArgumentNullException.ThrowIfNull(executor);
            return WithFallback((context, _) => Task.FromResult(executor(context)));
        }

        public Task<Result<TResult>> ExecuteAsync(CancellationToken cancellationToken = default) =>
            _gate.ExecuteAsync(
                _changeId,
                _minVersion,
                _maxVersion,
                (decision, ct) => ExecuteBranchAsync(decision, ct),
                _initialVersionProvider,
                cancellationToken);

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

        public VersionDecision Decision { get; }

        public string ChangeId => _changeId;

        public int Version => Decision.Version;

        public bool IsNew => Decision.IsNew;

        public DateTimeOffset RecordedAt => Decision.RecordedAt;

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

        public Task<Result<T>> CaptureAsync<T>(
            string stepId,
            Func<Task<Result<T>>> effect,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return CaptureAsync(stepId, _ => effect(), cancellationToken);
        }

        public Task<Result<T>> CaptureAsync<T>(
            string stepId,
            Func<Result<T>> effect,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return CaptureAsync(stepId, _ => Task.FromResult(effect()), cancellationToken);
        }

        public string CreateEffectId(string stepId)
        {
            var normalized = NormalizeScope(stepId);
            return BuildEffectId(_changeId, Version, normalized);
        }
    }

    private sealed record WorkflowBranch<TResult>(
        Func<VersionDecision, bool> Predicate,
        Func<DeterministicWorkflowContext, CancellationToken, Task<Result<TResult>>> Executor);
}
