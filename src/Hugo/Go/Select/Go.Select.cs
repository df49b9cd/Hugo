using System.Diagnostics;

namespace Hugo;

/// <content>
/// Implements channel selection helpers that mirror Go's `select` semantics.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Awaits the first channel case to produce a value.
    /// </summary>
    /// <param name="provider">The optional time provider used for instrumentation.</param>
    /// <param name="cancellationToken">The token used to cancel the select operation.</param>
    /// <param name="cases">The channel cases to observe.</param>
    /// <returns>A result indicating whether the select operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectAsync(TimeProvider? provider = null, CancellationToken cancellationToken = default, params ChannelCase[] cases) =>
        SelectInternalAsync(cases, defaultCase: null, Timeout.InfiniteTimeSpan, provider, cancellationToken);

    /// <summary>
    /// Awaits the first channel case to produce a value or returns when the timeout elapses.
    /// </summary>
    /// <param name="timeout">The duration to wait before timing out.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the select operation.</param>
    /// <param name="cases">The channel cases to observe.</param>
    /// <returns>A result indicating whether the select operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default, params ChannelCase[] cases) =>
        SelectInternalAsync(cases, defaultCase: null, timeout, provider, cancellationToken);

    /// <summary>
    /// Creates a fluent builder that materializes a typed channel select workflow.
    /// </summary>
    /// <typeparam name="TResult">The result type produced by the select continuation.</typeparam>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the select operation.</param>
    /// <returns>A builder that can configure and execute a select workflow.</returns>
    public static SelectBuilder<TResult> Select<TResult>(TimeProvider? provider = null, CancellationToken cancellationToken = default) =>
        new(Timeout.InfiniteTimeSpan, provider, cancellationToken);

    /// <summary>
    /// Creates a fluent builder that materializes a typed channel select workflow with a timeout.
    /// </summary>
    /// <typeparam name="TResult">The result type produced by the select continuation.</typeparam>
    /// <param name="timeout">The duration to wait before timing out.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the select operation.</param>
    /// <returns>A builder that can configure and execute a select workflow.</returns>
    public static SelectBuilder<TResult> Select<TResult>(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default) =>
        new(timeout, provider, cancellationToken);

    internal static async Task<Result<Unit>> SelectInternalAsync(IReadOnlyList<ChannelCase> cases, ChannelCase? defaultCase, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        provider ??= TimeProvider.System;

        ChannelCase? resolvedDefault = defaultCase;
        List<ChannelCase> caseList = new(cases.Count);
        foreach (ChannelCase channelCase in cases)
        {
            if (channelCase.IsDefault)
            {
                if (resolvedDefault.HasValue)
                {
                    throw new ArgumentException("Only one default case may be supplied.", nameof(cases));
                }

                resolvedDefault = channelCase;
                continue;
            }

            caseList.Add(channelCase);
        }

        if (caseList.Count == 0 && !resolvedDefault.HasValue)
        {
            throw new ArgumentException("At least one channel case must be provided.", nameof(cases));
        }

        using Activity? activity = GoDiagnostics.StartSelectActivity(caseList.Count + (resolvedDefault.HasValue ? 1 : 0), timeout);
        GoDiagnostics.RecordChannelSelectAttempt(activity);
        long startTimestamp = provider.GetTimestamp();

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Attempt immediate reads to honor priority without awaiting.
        List<(int Index, object? State)> immediateCandidates = [];
        for (int i = 0; i < caseList.Count; i++)
        {
            if (caseList[i].TryDequeueImmediately(out object? state))
            {
                immediateCandidates.Add((i, state));
            }
        }

        if (immediateCandidates.Count > 0)
        {
            (int selectedIndex, object? selectedState) = GoSelectHelpers.SelectByPriority(immediateCandidates, caseList);
            TimeSpan completionDuration = provider.GetElapsedTime(startTimestamp);

            try
            {
                Result<Unit> continuationResult = await caseList[selectedIndex].ContinueWithAsync(selectedState, linkedCts.Token).ConfigureAwait(false);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, continuationResult.IsSuccess ? null : continuationResult.Error);
                return continuationResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Error error = Error.FromException(ex);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                return Result.Fail<Unit>(error);
            }
            finally
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
            }
        }

        if (resolvedDefault.HasValue)
        {
            TimeSpan completionDuration = provider.GetElapsedTime(startTimestamp);
            try
            {
                Result<Unit> defaultResult = await resolvedDefault.Value.ContinueWithAsync(null, linkedCts.Token).ConfigureAwait(false);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, defaultResult.IsSuccess ? null : defaultResult.Error);
                return defaultResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Error error = Error.FromException(ex);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                return Result.Fail<Unit>(error);
            }
            finally
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
            }
        }

        List<Task<(bool HasValue, object? Value)>> waitTasks = new(caseList.Count);
        for (int i = 0; i < caseList.Count; i++)
        {
            waitTasks.Add(caseList[i].WaitAsync(linkedCts.Token));
        }

        Task? timeoutTask = timeout == Timeout.InfiniteTimeSpan
            ? null
            : provider.DelayAsync(timeout, linkedCts.Token);

        try
        {
            while (true)
            {
                if (waitTasks.Count == 0)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    TimeSpan drainedDuration = provider.GetElapsedTime(startTimestamp);
                    Error drainedError = Error.From("All channel cases completed without yielding a value.", ErrorCodes.SelectDrained);
                    GoDiagnostics.RecordChannelSelectCompleted(drainedDuration, activity, drainedError);
                    return Result.Fail<Unit>(drainedError);
                }

                Task completedTask;
                if (timeoutTask is null)
                {
                    completedTask = await Task.WhenAny(GoSelectHelpers.ToTaskArray(waitTasks)).ConfigureAwait(false);
                }
                else
                {
                    Task[] aggregate = new Task[waitTasks.Count + 1];
                    for (int i = 0; i < waitTasks.Count; i++)
                    {
                        aggregate[i] = waitTasks[i];
                    }

                    aggregate[^1] = timeoutTask;
                    completedTask = await Task.WhenAny(aggregate).ConfigureAwait(false);

                    if (completedTask == timeoutTask)
                    {
                        await linkedCts.CancelAsync().ConfigureAwait(false);
                        TimeSpan timeoutDuration = provider.GetElapsedTime(startTimestamp);
                        GoDiagnostics.RecordChannelSelectTimeout(timeoutDuration, activity);
                        return Result.Fail<Unit>(Error.Timeout(timeout));
                    }
                }

                List<int> completedIndices = GoSelectHelpers.CollectCompletedIndices(waitTasks);
                if (completedIndices.Count == 0)
                {
                    continue;
                }

                List<int> drainedIndices = [];
                List<(int Index, object? State)> ready = [];

                foreach (int idx in completedIndices)
                {
                    (bool hasValue, object? value) = await waitTasks[idx].ConfigureAwait(false);
                    if (hasValue)
                    {
                        ready.Add((idx, value));
                    }
                    else
                    {
                        drainedIndices.Add(idx);
                    }
                }

                if (ready.Count == 0)
                {
                    if (drainedIndices.Count > 0)
                    {
                        GoSelectHelpers.RemoveAtIndices(caseList, waitTasks, drainedIndices);
                    }

                    continue;
                }

                (int selectedIndex, object? selectedState) = GoSelectHelpers.SelectByPriority(ready, caseList);
                TimeSpan completionDuration = provider.GetElapsedTime(startTimestamp);

                try
                {
                    Result<Unit> continuationResult = await caseList[selectedIndex].ContinueWithAsync(selectedState, linkedCts.Token).ConfigureAwait(false);
                    GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, continuationResult.IsSuccess ? null : continuationResult.Error);
                    return continuationResult;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Error error = Error.FromException(ex);
                    GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                    return Result.Fail<Unit>(error);
                }
                finally
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException oce)
        {
            TimeSpan canceledDuration = provider.GetElapsedTime(startTimestamp);
            bool canceledByCaller = cancellationToken is { CanBeCanceled: true, IsCancellationRequested: true };
            GoDiagnostics.RecordChannelSelectCanceled(canceledDuration, activity, canceledByCaller);
            CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : oce.CancellationToken;
            return Result.Fail<Unit>(Error.Canceled(token: token.CanBeCanceled ? token : null));
        }
    }
}

internal static class GoSelectHelpers
{
    public static Task[] ToTaskArray(List<Task<(bool HasValue, object? Value)>> source)
    {
        Task[] array = new Task[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            array[i] = source[i];
        }

        return array;
    }

    public static List<int> CollectCompletedIndices(List<Task<(bool HasValue, object? Value)>> tasks)
    {
        List<int> indices = [];
        for (int i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].IsCompleted)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    public static (int Index, object? State) SelectByPriority(List<(int Index, object? State)> candidates, List<ChannelCase> cases)
    {
        (int selectedIndex, object? selectedState) = candidates[0];
        int bestPriority = cases[selectedIndex].Priority;

        for (int i = 1; i < candidates.Count; i++)
        {
            (int candidateIndex, object? candidateState) = candidates[i];
            int priority = cases[candidateIndex].Priority;
            if (priority < bestPriority || (priority == bestPriority && candidateIndex < selectedIndex))
            {
                selectedIndex = candidateIndex;
                selectedState = candidateState;
                bestPriority = priority;
            }
        }

        return (selectedIndex, selectedState);
    }

    public static void RemoveAtIndices(List<ChannelCase> cases, List<Task<(bool HasValue, object? Value)>> tasks, List<int> indices)
    {
        if (indices.Count == 0)
        {
            return;
        }

        indices.Sort();
        for (int i = indices.Count - 1; i >= 0; i--)
        {
            int index = indices[i];
            cases.RemoveAt(index);
            tasks.RemoveAt(index);
        }
    }
}
