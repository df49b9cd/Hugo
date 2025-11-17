namespace Hugo;

/// <summary>
/// Exception type thrown when converting a failed <see cref="Result{T}"/> into a value.
/// </summary>
public sealed class ResultException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ResultException"/> class.</summary>
    /// <param name="error">The error associated with the failed result.</param>
    public ResultException(Error error)
        : base(error?.Message ?? Error.Unspecified().Message, error?.Cause)
    {
        Error = error ?? Error.Unspecified();
        if (error?.Cause is not null)
        {
            HResult = error.Cause.HResult;
        }
    }

    /// <summary>Gets the <see cref="Error"/> that caused the exception to be thrown.</summary>
    public Error Error { get; }

    public ResultException()
        : this(Error.Unspecified())
    {
    }

    public ResultException(string message)
        : this(Error.From(message ?? Error.Unspecified().Message, ErrorCodes.Exception))
    {
    }

    public ResultException(string message, Exception innerException)
        : this(Error.From(message ?? innerException?.Message ?? Error.Unspecified().Message, ErrorCodes.Exception, innerException))
    {
    }
}
