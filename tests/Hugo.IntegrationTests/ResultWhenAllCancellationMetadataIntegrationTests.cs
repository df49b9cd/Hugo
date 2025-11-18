using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Hugo.Policies;

using Shouldly;

namespace Hugo.Tests;

public sealed class ResultWhenAllCancellationMetadataIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task BuildWhenAllCancellationErrorAsync_ShouldCapturePartialFailuresAndCompensationErrors()
    {
        var pipelineScope = new CompensationScope();
        pipelineScope.Register(_ => ValueTask.CompletedTask);

        var policy = ResultExecutionPolicy.None.WithCompensation(new ResultCompensationPolicy(_ => throw new InvalidOperationException("compensation-failure")));

        var completed = new[] { true, false, true };
        var results = new Result.PipelineOperationResult<int>[3];

        var successfulCompensation = new CompensationScope();
        successfulCompensation.Register(_ => ValueTask.CompletedTask);
        results[0] = new Result.PipelineOperationResult<int>(Result.Ok(1), successfulCompensation);
        results[2] = new Result.PipelineOperationResult<int>(Result.Fail<int>(Error.From("partial-failure")), new CompensationScope());

        var operations = new ValueTask<Result.PipelineOperationResult<int>>[3];
        operations[0] = ValueTask.FromResult(results[0]);
        operations[1] = new ValueTask<Result.PipelineOperationResult<int>>(Task.FromCanceled<Result.PipelineOperationResult<int>>(new CancellationToken(true)));
        operations[2] = ValueTask.FromResult(results[2]);

        using var fallbackCts = new CancellationTokenSource();
        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();

        var method = typeof(Result)
            .GetMethod("BuildWhenAllCancellationErrorAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));

        var cancellationError = await (ValueTask<Error>)method.Invoke(
            obj: null,
            parameters: new object[]
            {
                operations,
                results,
                completed,
                pipelineScope,
                policy,
                new OperationCanceledException(canceledCts.Token),
                fallbackCts.Token
            })!;

        cancellationError.Code.ShouldBe(ErrorCodes.Canceled);
        cancellationError.Metadata.TryGetValue("whenall.partialFailures", out var partialFailures).ShouldBeTrue();

        var failures = partialFailures.ShouldBeOfType<Error[]>();
        failures.ShouldContain(error => error.Code == ErrorCodes.Canceled);
        failures.ShouldContain(error => error.Message.Contains("partial-failure", StringComparison.Ordinal));
        failures.ShouldContain(error => error.Code == ErrorCodes.Exception);
    }

    [Fact(Timeout = 30_000)]
    public void ResultStruct_ShouldExposeCompensationAndConversions()
    {
        Should.Throw<ArgumentNullException>(() => Result.Ok(1).WithCompensation((Func<CancellationToken, ValueTask>)null!));

        var result = Result.Ok(10)
            .WithCompensation(_ => ValueTask.CompletedTask)
            .WithCompensation("state", (_, _) => ValueTask.CompletedTask);

        result.HasCompensation.ShouldBeTrue();
        result.TryGetCompensation(out var scope).ShouldBeTrue();
        scope.ShouldNotBeNull();
        scope!.HasActions.ShouldBeTrue();

        var roundTripped = result.ToResult();
        roundTripped.ShouldBe(result);

        var tuple = roundTripped.ToValueTuple();
        tuple.Value.ShouldBe(10);
        tuple.Error.ShouldBeNull();

        Should.Throw<ArgumentNullException>(() => result.Switch(null!, _ => { }));
    }

    [Fact(Timeout = 30_000)]
    public void ResultCollections_ShouldAggregateAndShortCircuit()
    {
        var sequence = new[]
        {
            Result.Ok(1),
            Result.Fail<int>(Error.From("halt")),
            Result.Ok(3)
        };

        var sequenceResult = Result.Sequence(sequence);
        sequenceResult.IsFailure.ShouldBeTrue();
        sequenceResult.Error.ShouldNotBeNull();

        var traversal = Result.Traverse<int, int>(
            new[] { 1, 2, 3 },
            value => value < 3 ? Result.Ok(value * 2) : Result.Fail<int>(Error.From("traverse-stop")));

        traversal.IsFailure.ShouldBeTrue();
        traversal.Error.ShouldNotBeNull();
        traversal.Error!.Message.ShouldContain("traverse-stop");
    }
}
