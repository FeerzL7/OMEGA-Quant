using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Risk;
using Omega.Strategy;

namespace Omega.Backtesting;

public enum ExitReason
{
    StopLoss = 1,
    TakeProfit = 2,
    Signal = 3,
    TimeLimit = 4,
    EndOfData = 5,
}

/// <summary>A completed round trip. Prices are fill prices (costs included); PnL is net of fees.</summary>
public sealed record BacktestTrade(
    DateTimeOffset EntryCandleOpenTimeUtc,
    decimal EntryPrice,
    decimal Quantity,
    decimal EntryFee,
    decimal StopLossPrice,
    decimal? TakeProfitPrice,
    DateTimeOffset ExitCandleOpenTimeUtc,
    decimal ExitPrice,
    decimal ExitFee,
    ExitReason ExitReason,
    int HoldingCandles,
    decimal NetPnl,
    string? EntryExplanation)
{
    /// <summary>Net PnL relative to the capital committed at entry (position cost plus entry fee).</summary>
    public decimal ReturnOnCost => NetPnl / ((EntryPrice * Quantity) + EntryFee);
}

/// <summary>Equity marked at a candle close (cash + position × close).</summary>
public sealed record EquityPoint(DateTimeOffset TimeUtc, decimal Equity, decimal Drawdown);

/// <summary>A signal that could not be acted on.</summary>
public sealed record BacktestRejection(DateTimeOffset TimeUtc, string Code, string Detail);

public static class RejectionCodes
{
    public const string ShortNotSupportedOnSpot = "SHORT_NOT_SUPPORTED_ON_SPOT";
    public const string StopNotBelowEntry = "STOP_NOT_BELOW_ENTRY";
    public const string TakeProfitNotAboveEntry = "TAKE_PROFIT_NOT_ABOVE_ENTRY";
    // Entries refused by the Risk Engine use RiskDecision.Code (RISK_...), e.g. RISK_POSITION_TOO_SMALL.
    public const string SignalExpiredByDataGap = "SIGNAL_EXPIRED_BY_DATA_GAP";
}

/// <summary>Identifies the exact candles a backtest ran on (CLAUDE.md §22).</summary>
public sealed record DatasetFingerprint(
    string Symbol, CandleInterval Interval, DateTimeOffset FirstOpenTimeUtc, DateTimeOffset LastOpenTimeUtc, int CandleCount, string Sha256)
{
    /// <summary>SHA-256 over every candle's open time and values; numbers normalized (no trailing zeros).</summary>
    public static DatasetFingerprint From(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0)
        {
            throw new ArgumentException("No candles.", nameof(candles));
        }

        var text = new StringBuilder();
        foreach (var c in candles)
        {
            text.Append(c.OpenTimeUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(N(c.Open)).Append('|').Append(N(c.High)).Append('|').Append(N(c.Low)).Append('|').Append(N(c.Close)).Append('|')
                .Append(N(c.BaseVolume)).Append('|').Append(N(c.QuoteVolume)).Append('|')
                .Append(c.TradeCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return new DatasetFingerprint(
            candles[0].Symbol, candles[0].Interval, candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc, candles.Count,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));

        static string N(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    }
}

/// <summary>Everything needed to reproduce and judge a backtest.</summary>
/// <remarks>
/// <c>Risk</c> and <c>RiskSummary</c> are always set by the engine since Phase 10; they are null only in runs stored
/// before that, read back from the database.
/// </remarks>
public sealed record BacktestResult(
    StrategyIdentity Strategy,
    string FeatureSetVersion,
    string FeatureSetHash,
    BacktestConfig Config,
    DatasetFingerprint Dataset,
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<EquityPoint> EquityCurve,
    IReadOnlyList<BacktestRejection> Rejections,
    IReadOnlyDictionary<NoTradeReason, int> NoTradeCounts,
    IReadOnlyList<string> Warnings,
    BacktestMetrics Metrics,
    TradeStatistics Statistics,
    RiskLimits? Risk,
    RiskSummary? RiskSummary);

/// <summary>What the Risk Engine did during a backtest.</summary>
/// <param name="RejectionsByCheck">Entries refused, by risk code (e.g. RISK_DAILY_LOSS_LIMIT).</param>
/// <param name="KillSwitchTrippedAtUtc">When the kill switch tripped, if it did (no new entries after it).</param>
/// <param name="KillSwitchReason">Why it tripped.</param>
public sealed record RiskSummary(IReadOnlyDictionary<string, int> RejectionsByCheck, DateTimeOffset? KillSwitchTrippedAtUtc, string? KillSwitchReason);
