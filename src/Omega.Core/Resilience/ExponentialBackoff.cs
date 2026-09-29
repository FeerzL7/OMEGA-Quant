namespace Omega.Core.Resilience;

/// <summary>
/// Exponential backoff with "equal jitter": the delay for attempt n is a random
/// value in [d/2, d], where d = min(initial * 2^n, max). The lower bound avoids
/// hammering a failing dependency (exchange, database); the jitter spreads retries out.
/// </summary>
public sealed class ExponentialBackoff
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Random _random;

    public ExponentialBackoff(TimeSpan initialDelay, TimeSpan maxDelay, Random random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, initialDelay);
        ArgumentNullException.ThrowIfNull(random);

        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
        _random = random;
    }

    /// <param name="attempt">Zero-based number of consecutive failed attempts.</param>
    public TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        var exponentialMs = _initialDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 30));
        var cappedMs = Math.Min(exponentialMs, _maxDelay.TotalMilliseconds);
        var halfMs = cappedMs / 2;

        return TimeSpan.FromMilliseconds(halfMs + (halfMs * _random.NextDouble()));
    }
}
