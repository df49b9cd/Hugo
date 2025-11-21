
namespace Hugo.Tests;

public sealed class ResultExceptionTests
{
    [Fact(Timeout = 5_000)]
    public void Ctor_WithInnerException_ShouldCopyHResult()
    {
        var inner = new InvalidOperationException("boom")
        {
            HResult = unchecked((int)0x80131509)
        };

        var error = Error.From("wrapped", ErrorCodes.Exception, inner);

        var exception = new ResultException(error);

        exception.Error.ShouldBeSameAs(error);
        exception.HResult.ShouldBe(inner.HResult);
        exception.InnerException.ShouldBe(inner);
    }

    [Fact(Timeout = 5_000)]
    public void DefaultCtor_ShouldPopulateUnspecifiedError()
    {
        var exception = new ResultException();

        exception.Error.ShouldNotBeNull();
        exception.Error.Code.ShouldBe(ErrorCodes.Unspecified);
    }
}
