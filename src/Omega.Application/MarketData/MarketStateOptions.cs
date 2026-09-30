namespace Omega.Application.MarketData;

/// <summary>Options bound from the <c>MarketState</c> configuration section.</summary>
public sealed class MarketStateOptions
{
    public const string SectionName = "MarketState";

    /// <summary>
    /// Tolerated delay after the next candle's expected close before the data is
    /// considered stale. Covers delivery latency and short reconnections.
    /// </summary>
    public TimeSpan FreshnessGracePeriod { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A gap within this window makes the market data unreliable.</summary>
    public TimeSpan IntegrityWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the worker re-evaluates freshness.</summary>
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (FreshnessGracePeriod < TimeSpan.Zero || FreshnessGracePeriod > TimeSpan.FromHours(1))
        {
            errors.Add("MarketState:FreshnessGracePeriod must be between 0 and 1 hour.");
        }

        if (IntegrityWindow <= TimeSpan.Zero || IntegrityWindow > TimeSpan.FromDays(366))
        {
            errors.Add("MarketState:IntegrityWindow must be positive and at most 366 days.");
        }

        if (EvaluationInterval < TimeSpan.FromSeconds(1) || EvaluationInterval > TimeSpan.FromMinutes(10))
        {
            errors.Add("MarketState:EvaluationInterval must be between 1 second and 10 minutes.");
        }

        return errors;
    }
}

/// <summary>Options bound from the <c>MarketData:Backfill</c> configuration section.</summary>
public sealed class BackfillOptions
{
    public const string SectionName = "MarketData:Backfill";

    /// <summary>Fill gaps from the exchange's historical endpoint. Disable to only detect and record them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>At start-up, gaps within this period are filled (older ones are left as recorded).</summary>
    public TimeSpan StartupLookback { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Attempts per gap for transient failures (network, timeout, rate limit).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Optional start of the historical data to keep (for backtests). When set, the worker imports, at start-up,
    /// every closed candle from this date to the oldest stored one (or to now if nothing is stored). Idempotent.
    /// </summary>
    public DateTimeOffset? HistoryStart { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (StartupLookback <= TimeSpan.Zero || StartupLookback > TimeSpan.FromDays(366))
        {
            errors.Add("MarketData:Backfill:StartupLookback must be positive and at most 366 days.");
        }

        if (MaxAttempts is < 1 or > 10)
        {
            errors.Add("MarketData:Backfill:MaxAttempts must be between 1 and 10.");
        }

        // Binance Spot BTCUSDT history starts in 2017; earlier dates only add empty requests.
        if (HistoryStart is { } start && (start < new DateTimeOffset(2017, 1, 1, 0, 0, 0, TimeSpan.Zero) || start.Offset != TimeSpan.Zero))
        {
            errors.Add("MarketData:Backfill:HistoryStart must be a UTC date on or after 2017-01-01.");
        }

        return errors;
    }
}
