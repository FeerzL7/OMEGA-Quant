namespace Omega.Core.Trading;

/// <summary>
/// Direction of a strategy signal.
/// </summary>
/// <remarks>
/// The value 0 is intentionally not defined. <c>default(SignalDirection)</c> is
/// therefore not a valid member and can be rejected with
/// <see cref="Enum.IsDefined{TEnum}(TEnum)"/>, instead of silently meaning
/// <see cref="Long"/>. A <see cref="NoTrade"/> signal carries an explicit
/// <see cref="NoTradeReason"/>; see <see cref="Signal"/>.
/// </remarks>
public enum SignalDirection
{
    Long = 1,
    Short = 2,
    Hold = 3,
    NoTrade = 4,
}
