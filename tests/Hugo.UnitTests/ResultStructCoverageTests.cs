using System.Threading.Tasks;

using Shouldly;

namespace Hugo.Tests;

public sealed class ResultStructCoverageTests
{
    [Fact(Timeout = 5_000)]
    public void CastFailure_ShouldThrowForSuccessfulResult()
    {
        var result = Result.Ok(1);

        Should.Throw<InvalidOperationException>(() => result.CastFailure<string>());
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask SwitchAsync_ShouldHonorCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = Result.Ok(1);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await result.SwitchAsync(
                (_, _) => ValueTask.CompletedTask,
                (_, _) => ValueTask.CompletedTask,
                cts.Token));
    }

    [Fact(Timeout = 5_000)]
    public void WithCompensation_ShouldCaptureStatefulAction()
    {
        var invoked = false;
        var result = Result.Ok(1).WithCompensation("state", (_, _) =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        });

        result.IsSuccess.ShouldBeTrue();
        invoked.ShouldBeFalse();
    }

    [Fact(Timeout = 5_000)]
    public void ValueOr_WithFactory_ShouldUseProvidedFallback()
    {
        var failure = Result.Fail<int>(Error.From("missing"));
        var recovered = failure.ValueOr(error => error.Message.Length);

        recovered.ShouldBe("missing".Length);
    }

    [Fact(Timeout = 5_000)]
    public void TupleConversions_ShouldRoundTripValueAndError()
    {
        var failure = Result.Fail<int>(Error.From("tuple-error"));
        (int Value, Error? Error) tuple = failure;

        tuple.Value.ShouldBe(default);
        tuple.Error.ShouldBeSameAs(failure.Error);

        Result<int> fromTuple = tuple;
        fromTuple.IsFailure.ShouldBeTrue();
        fromTuple.Error.ShouldBeSameAs(tuple.Error);
    }

    [Fact(Timeout = 5_000)]
    public void Switch_ShouldEnforceNonNullCallbacks()
    {
        var result = Result.Ok(1);

        Should.Throw<ArgumentNullException>(() => result.Switch(null!, _ => { }));
        Should.Throw<ArgumentNullException>(() => result.Switch(_ => { }, null!));
    }

    [Fact(Timeout = 5_000)]
    public void WithCompensation_ShouldExposeAggregatedScope()
    {
        var result = Result.Ok(1)
            .WithCompensation(_ => ValueTask.CompletedTask)
            .WithCompensation("stateful", (_, _) => ValueTask.CompletedTask);

        result.HasCompensation.ShouldBeTrue();
        result.TryGetCompensation(out var scope).ShouldBeTrue();
        scope.ShouldNotBeNull();
        scope!.HasActions.ShouldBeTrue();
    }

    [Fact(Timeout = 5_000)]
    public void ToResultAndValueTuple_ShouldRoundTripState()
    {
        var failure = Result.Fail<int>(Error.From("tuple-roundtrip"));

        var roundTripped = failure.ToResult();
        roundTripped.ShouldBe(failure);

        var tuple = roundTripped.ToValueTuple();
        tuple.Value.ShouldBe(default);
        tuple.Error.ShouldBeSameAs(failure.Error);
    }
}
