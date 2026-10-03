namespace Omega.UI.Presentation;

/// <summary>Section <c>Dashboard</c>.</summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>How often panels ask the API for new data. Candles close every 5 minutes; seconds of delay are irrelevant.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(4);

    public string Symbol { get; set; } = "BTCUSDT";

    public string Interval { get; set; } = "5m";

    public int ChartCandles { get; set; } = 150;

    /// <summary>Tolerance after a candle's expected close before it counts as late.</summary>
    public TimeSpan FreshnessGrace { get; set; } = TimeSpan.FromMinutes(1);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RefreshInterval < TimeSpan.FromSeconds(1)) errors.Add("Dashboard:RefreshInterval must be at least 1 second.");
        if (ApiTimeout <= TimeSpan.Zero || ApiTimeout >= RefreshInterval) errors.Add("Dashboard:ApiTimeout must be positive and shorter than the refresh interval.");
        if (ChartCandles is < 10 or > 1_000) errors.Add("Dashboard:ChartCandles must be between 10 and 1000.");
        if (Interval != "5m") errors.Add("Dashboard:Interval: only 5m is supported.");
        return errors;
    }

    public TimeSpan IntervalLength => TimeSpan.FromMinutes(5);
}
