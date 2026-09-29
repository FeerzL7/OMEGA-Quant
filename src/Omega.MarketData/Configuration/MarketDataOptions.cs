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
