using System.Globalization;

namespace Omega.UI.Presentation;

/// <summary>
/// How numbers and times are shown (CLAUDE.md §25: clear units, clear timestamps, no misleading values). All times in
/// UTC with an explicit suffix; missing values are an em dash, never zero.
/// </summary>
public static class Display
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-MX");

    public const string Missing = "—";

    public static string Utc(DateTimeOffset? time) =>
        time is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : Missing;

    public static string Price(decimal? value) => value is { } v ? v.ToString("#,##0.00", Culture) : Missing;

    public static string Quantity(decimal? value) => value is { } v ? v.ToString("0.########", Culture) : Missing;

    public static string Money(decimal? value) => value is { } v ? v.ToString("#,##0.00", Culture) + " USDT" : Missing;

    public static string SignedMoney(decimal? value) => value is { } v ? (v > 0 ? "+" : string.Empty) + v.ToString("#,##0.00", Culture) + " USDT" : Missing;

    /// <summary>0.0123 → "1.23 %"; signed when asked.</summary>
    public static string Percent(double? fraction, int decimals = 2, bool signed = false) =>
        fraction is { } f && double.IsFinite(f)
            ? (signed && f > 0 ? "+" : string.Empty) + (f * 100).ToString("F" + decimals, Culture) + " %"
            : Missing;

    public static string Percent(decimal? fraction, int decimals = 2, bool signed = false) => Percent((double?)fraction, decimals, signed);

    /// <summary>"hace 12 s", "hace 3 min", "hace 2 h 5 min", "hace 3 d".</summary>
    public static string Age(TimeSpan? age)
    {
        if (age is not { } a)
        {
            return Missing;
        }

        if (a < TimeSpan.Zero)
        {
            return "en el futuro (revisar reloj)";
        }

        return a.TotalSeconds < 60 ? $"hace {a.TotalSeconds:0} s"
            : a.TotalMinutes < 60 ? $"hace {a.TotalMinutes:0} min"
            : a.TotalHours < 24 ? $"hace {(int)a.TotalHours} h {a.Minutes} min"
            : $"hace {(int)a.TotalDays} d";
    }

    public static string Direction(string? direction) => direction switch
    {
        "Long" => "LONG",
        "Short" => "SHORT",
        "Hold" => "HOLD",
        "NoTrade" => "NO_TRADE",
        null => Missing,
        _ => direction,
    };
}

/// <summary>Whether data shown on screen can still be read as current.</summary>
public enum Freshness
{
    Unknown = 0,
    Fresh = 1,
    Late = 2,
    Stale = 3,
}

public static class DataAge
{
    /// <summary>
    /// A 5-minute candle closes every 5 minutes: up to one interval plus a grace after its close it is fresh; up to three
    /// intervals it is late; older it is stale and must be shown as such.
    /// </summary>
    public static Freshness OfCandle(DateTimeOffset? closeTimeUtc, DateTimeOffset now, TimeSpan interval, TimeSpan grace)
    {
        if (closeTimeUtc is not { } close)
        {
            return Freshness.Unknown;
        }

        var age = now - close;
        return age <= interval + grace ? Freshness.Fresh : age <= interval * 3 ? Freshness.Late : Freshness.Stale;
    }

    /// <summary>An API answer older than three refresh periods is no longer current (the API stopped answering).</summary>
    public static Freshness OfResponse(DateTimeOffset? receivedAtUtc, DateTimeOffset now, TimeSpan refresh) =>
        receivedAtUtc is not { } received ? Freshness.Unknown : now - received <= refresh * 3 ? Freshness.Fresh : Freshness.Stale;

    public static string Label(Freshness freshness) => freshness switch
    {
        Freshness.Fresh => "al día",
        Freshness.Late => "retrasado",
        Freshness.Stale => "desactualizado",
        _ => "sin datos",
    };
}

/// <summary>The numbers a model strategy attaches to each decision (Signal.Metrics), when present.</summary>
public sealed record DecisionNumbers(double? RawProbability, double? CalibratedProbability, double? ExpectedReturn, double? Gain, double? Loss, double? Costs)
{
    public static DecisionNumbers From(IReadOnlyDictionary<string, double>? metrics) => new(
        Get(metrics, "raw_probability"), Get(metrics, "calibrated_probability"), Get(metrics, "expected_return"),
        Get(metrics, "gain"), Get(metrics, "loss"), Get(metrics, "costs"));

    private static double? Get(IReadOnlyDictionary<string, double>? metrics, string key) =>
        metrics is not null && metrics.TryGetValue(key, out var value) ? value : null;
}
