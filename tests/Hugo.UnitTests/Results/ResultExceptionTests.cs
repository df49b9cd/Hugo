using Shouldly;

namespace Hugo.Tests.Results;

public sealed class ResultExceptionTests
{
    [Fact(Timeout = 15_000)]
    public void Ctor_ShouldPropagateInnerHResult_WhenErrorHasCause()
    {
        var inner = new InvalidOperationException("boom") { HResult = unchecked((int)0x80004005) };
        var error = Error.From("wrapped", ErrorCodes.Exception, inner);

        var exception = new ResultException(error);

        exception.HResult.ShouldBe(inner.HResult);
        exception.Error.ShouldBe(error);
        exception.InnerException.ShouldBe(inner);
    }

    [Fact(Timeout = 15_000)]
    public void Ctor_WithMessage_ShouldCreateExceptionError()
    {
        var exception = new ResultException("explicit message");

        exception.Error.Message.ShouldBe("explicit message");
        exception.Error.Code.ShouldBe(ErrorCodes.Exception);
    }
}
