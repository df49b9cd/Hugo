namespace Hugo;

/// <summary>
/// Well-known error codes emitted by the library.
/// </summary>
public static class ErrorCodes
{
    public const string Unspecified = "error.unspecified";
    public const string Canceled = "error.canceled";
    public const string Timeout = "error.timeout";
    public const string Exception = "error.exception";
    public const string Aggregate = "error.aggregate";
    public const string Validation = "error.validation";
    public const string SelectDrained = "error.select.drained";
}
