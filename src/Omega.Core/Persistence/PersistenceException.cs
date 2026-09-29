namespace Omega.Core.Persistence;

/// <summary>
/// A storage operation failed. Implementations translate driver-specific errors
/// into this type so callers can decide whether retrying makes sense without
/// knowing which database is behind the abstraction.
/// </summary>
public sealed class PersistenceException : Exception
{
    public PersistenceException(string message, bool isTransient, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }

    public PersistenceException()
    {
    }

    public PersistenceException(string message)
        : base(message)
    {
    }

    public PersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// True when the same operation may succeed if retried (connection lost,
    /// timeout, server restarting). False for errors that retrying cannot fix,
    /// such as a violated integrity constraint.
    /// </summary>
    public bool IsTransient { get; }
}
