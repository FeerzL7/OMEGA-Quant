namespace Omega.Core.Results;

/// <summary>
/// A machine-readable error code plus a human-readable message.
/// </summary>
public sealed record Error
{
    public Error(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
    }

    public string Code { get; }

    public string Message { get; }
}
