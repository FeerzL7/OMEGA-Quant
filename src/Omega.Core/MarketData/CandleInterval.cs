namespace Omega.Core.MarketData;

/// <summary>
/// Candle aggregation interval. Only the intervals OMEGA actually uses are defined.
/// </summary>
/// <remarks>
/// The value 0 is intentionally not defined, so an unset interval is rejected
/// instead of silently meaning the first member.
/// </remarks>
public enum CandleInterval
{
    FiveMinutes = 1,
}

public static class CandleIntervalExtensions
{
    public static TimeSpan ToTimeSpan(this CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMinutes => TimeSpan.FromMinutes(5),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unsupported candle interval."),
    };

    /// <summary>Stable storage code (for example <c>5m</c>). Never reuse or change a code once data exists.</summary>
    public static string ToCode(this CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMinutes => "5m",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unsupported candle interval."),
    };

    public static bool TryParseCode(string? code, out CandleInterval interval)
    {
        switch (code)
        {
            case "5m":
                interval = CandleInterval.FiveMinutes;
                return true;
            default:
                interval = default;
                return false;
        }
    }
}
