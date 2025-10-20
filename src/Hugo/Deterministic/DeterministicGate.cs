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

    private static string BuildEffectId(string changeId, int version) => $"{changeId}::v{version}";
}
