namespace Omega.Risk;

/// <summary>
/// Stops all new entries. It is tripped automatically (maximum drawdown) or manually, and it is never reset
/// automatically: a person must decide that trading may resume. Open positions keep their stop losses.
/// </summary>
public sealed class KillSwitch
{
    public bool IsActive { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset? TrippedAtUtc { get; private set; }

    public string? ResetBy { get; private set; }

    /// <summary>Trips the switch. Tripping an active switch keeps the first reason and time.</summary>
    public void Trip(string reason, DateTimeOffset timeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        Reason = reason;
        TrippedAtUtc = timeUtc;
        ResetBy = null;
    }

    /// <summary>Manual reset. Requires naming who resets it, for the audit trail.</summary>
    public void Reset(string resetBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resetBy);
        IsActive = false;
        Reason = null;
        TrippedAtUtc = null;
        ResetBy = resetBy;
    }
}
