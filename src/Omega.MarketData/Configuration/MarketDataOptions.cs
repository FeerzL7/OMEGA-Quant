using System.Text.RegularExpressions;
using Omega.Core.MarketData;

namespace Omega.MarketData.Configuration;

/// <summary>
/// Options bound from the <c>MarketData</c> configuration section.
/// Market-data streams are public: this section never holds credentials.
/// </summary>
public sealed partial class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>
    /// Base WebSocket endpoint. The default serves market data only (no user or
    /// account data is available from it). See docs/decisions/ADR-003-binance.md.
    /// </summary>
    public string StreamBaseUrl { get; set; } = "wss://data-stream.binance.vision";

    /// <summary>
    /// Base REST endpoint for historical candles (gap back-fill). The default serves
    /// public market data only. See docs/decisions/ADR-008-market-state-and-backfill.md.
    /// </summary>
    public string RestBaseUrl { get; set; } = "https://data-api.binance.vision";

    /// <summary>Timeout of one REST request.</summary>
    public TimeSpan RestRequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tolerated difference between the local clock and exchange timestamps. A candle
    /// reported closed whose close time is further in the future than this reveals a
    /// local clock behind the exchange; REST candles are only accepted as closed once
    /// their close time is at least this far in the past.
    /// </summary>
    public TimeSpan ClockSkewTolerance { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Exchange symbol in upper case.</summary>
    public string Symbol { get; set; } = "BTCUSDT";

    public CandleInterval Interval { get; set; } = CandleInterval.FiveMinutes;

    /// <summary>Maximum time allowed to establish a connection.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// If no message arrives within this time, the connection is treated as dead
    /// and replaced. Binance pushes 5m kline updates every 2 seconds.
    /// </summary>
    public TimeSpan ReceiveIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// After this lifetime the connection is renewed right after a candle closes,
    /// instead of waiting for the exchange to drop it at the 24-hour mark at an
    /// arbitrary moment (possibly while a candle is closing).
    /// </summary>
    public TimeSpan MaxConnectionLifetime { get; set; } = TimeSpan.FromHours(23);

    /// <summary>Returns every configuration problem found; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(StreamBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "wss")
        {
            errors.Add("MarketData:StreamBaseUrl must be an absolute wss:// URL.");
        }

        if (!Uri.TryCreate(RestBaseUrl, UriKind.Absolute, out var restUri) || restUri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("MarketData:RestBaseUrl must be an absolute https:// URL.");
        }

        if (RestRequestTimeout <= TimeSpan.Zero)
        {
            errors.Add("MarketData:RestRequestTimeout must be positive.");
        }

        if (ClockSkewTolerance < TimeSpan.Zero || ClockSkewTolerance > TimeSpan.FromMinutes(1))
        {
            errors.Add("MarketData:ClockSkewTolerance must be between 0 and 1 minute.");
        }

        if (string.IsNullOrWhiteSpace(Symbol) || !SymbolPattern().IsMatch(Symbol))
        {
            errors.Add("MarketData:Symbol must contain only upper-case letters and digits (for example BTCUSDT).");
        }

        if (!Enum.IsDefined(Interval))
        {
            errors.Add("MarketData:Interval is not a supported candle interval.");
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            errors.Add("MarketData:ConnectTimeout must be positive.");
        }

        if (ReceiveIdleTimeout <= TimeSpan.Zero)
        {
            errors.Add("MarketData:ReceiveIdleTimeout must be positive.");
        }

        if (ReconnectInitialDelay <= TimeSpan.Zero)
        {
            errors.Add("MarketData:ReconnectInitialDelay must be positive.");
        }

        if (ReconnectMaxDelay < ReconnectInitialDelay)
        {
            errors.Add("MarketData:ReconnectMaxDelay must be greater than or equal to ReconnectInitialDelay.");
        }

        if (MaxConnectionLifetime <= TimeSpan.Zero || MaxConnectionLifetime >= TimeSpan.FromHours(24))
        {
            errors.Add("MarketData:MaxConnectionLifetime must be positive and shorter than 24 hours.");
        }

        return errors;
    }

    [GeneratedRegex("^[A-Z0-9]+$")]
    private static partial Regex SymbolPattern();
}
