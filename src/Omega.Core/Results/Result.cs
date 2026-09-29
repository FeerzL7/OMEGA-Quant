namespace Omega.Core.Results;

/// <summary>
/// Outcome of an operation that can fail in an expected, non-exceptional way.
/// A success never carries an error; a failure always does.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess && error is not null)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error is null)
        {
            throw new ArgumentNullException(nameof(error), "A failed result must carry an error.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(value, true, null);

    public static Result<T> Failure<T>(Error error) => new(default, false, error);
}

/// <summary>
/// Outcome of an operation that produces a value when it succeeds.
/// </summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, bool isSuccess, Error? error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>
    /// The produced value. Reading it from a failed result throws, so a failure
    /// can never be mistaken for a default value.
    /// </summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error!.Code}).");
}
