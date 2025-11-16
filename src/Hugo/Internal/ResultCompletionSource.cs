using System.Threading.Tasks;

namespace Hugo;

internal sealed class ResultCompletionSource<T>
{
    private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ValueTask<T> ValueTask => new(_source.Task);

    internal bool IsCompletedSuccessfully => _source.Task.IsCompletedSuccessfully;

    internal bool IsCanceled => _source.Task.IsCanceled;

    internal bool TrySetResult(T value) => _source.TrySetResult(value);

    internal bool TrySetCanceled(CancellationToken cancellationToken) => _source.TrySetCanceled(cancellationToken);

    internal bool TrySetException(Exception exception) => _source.TrySetException(exception);
}
