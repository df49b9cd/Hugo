using System.Runtime.CompilerServices;

using Shouldly;

namespace Hugo.Tests;

public sealed class ResultCollectionsTests
{
    [Fact(Timeout = 5_000)]
    public void Sequence_ShouldThrowWhenSourceIsNull()
    {
        Should.Throw<ArgumentNullException>(() => Result.Sequence<int>(null!));
    }

    [Fact(Timeout = 5_000)]
    public void Sequence_ShouldReturnFailureOnFirstFailure()
    {
        var sequence = new[]
        {
            Result.Ok(1),
            Result.Fail<int>(Error.From("first failure")),
            Result.Ok(2)
        };

        var result = Result.Sequence(sequence);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error?.Message.ShouldContain("first failure");
    }

    [Fact(Timeout = 5_000)]
    public void Traverse_ShouldGuardNullArguments()
    {
        Should.Throw<ArgumentNullException>(() => Result.Traverse<int, int>(null!, _ => Result.Ok(1)));
        Should.Throw<ArgumentNullException>(() => Result.Traverse<int, int>([1], null!));
    }

    [Fact(Timeout = 5_000)]
    public void Traverse_ShouldShortCircuitOnSelectorFailure()
    {
        var result = Result.Traverse<int, int>(
            new[] { 1, 2, 3 },
            value => value < 2 ? Result.Ok(value) : Result.Fail<int>(Error.From("stop")));

        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldContain("stop");
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask SequenceAsync_ShouldReturnCanceledErrorWhenEnumerationCancels()
    {
        async IAsyncEnumerable<Result<int>> Canceling([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return Result.Ok(1);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Result.SequenceAsync(Canceling(cts.Token), cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask SequenceAsync_ShouldTranslateEnumeratorExceptions()
    {
        async IAsyncEnumerable<Result<int>> Throwing([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162
            yield return Result.Ok(1);
#pragma warning restore CS0162
        }

        var result = await Result.SequenceAsync(Throwing(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask TraverseAsync_ShouldGuardArgumentsAndSurfaceCancellation()
    {
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await Result.TraverseAsync<int, int>((IAsyncEnumerable<int>)null!, (_, _) => ValueTask.FromResult(Result.Ok(0))));

        async IAsyncEnumerable<int> Single([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            await Task.Yield();
        }

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await Result.TraverseAsync<int, int>(Single(), null!));

        async IAsyncEnumerable<int> Canceling([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return 1;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canceled = await Result.TraverseAsync<int, int>(Canceling(cts.Token), (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result.Ok(1));
        }, cts.Token);

        canceled.IsFailure.ShouldBeTrue();
        canceled.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask TraverseAsync_ShouldTranslateSelectorException()
    {
        async IAsyncEnumerable<int> Values([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            await Task.Yield();
        }

        var result = await Result.TraverseAsync<int, int>(
            Values(TestContext.Current.CancellationToken),
            (_, _) => throw new InvalidOperationException("selector crash"),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }
}
