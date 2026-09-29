namespace Omega.Core.Trading;

/// <summary>
/// Direction of a strategy signal.
/// </summary>
/// <remarks>
/// The value 0 is intentionally not defined. <c>default(SignalDirection)</c> is
/// therefore not a valid member and can be rejected with
/// <see cref="Enum.IsDefined{TEnum}(TEnum)"/>, instead of silently meaning
/// <see cref="Long"/>. A <see cref="NoTrade"/> signal must carry an explicit
/// reason; that reason model belongs to the strategy phases and is not defined yet.
/// </remarks>
public enum SignalDirection
{
    Long = 1,
    Short = 2,
    Hold = 3,
    NoTrade = 4,
}
