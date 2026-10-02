using Omega.Core.Trading;

namespace Omega.Risk;

/// <summary>The control that rejected an entry.</summary>
public enum RiskCheck
{
    KillSwitch = 1,
    DailyLossLimit = 2,
    ConsecutiveLosses = 3,
    MaxOpenPositions = 4,
    MaxExposure = 5,
    ExcessiveSpread = 6,
    ExcessiveSlippage = 7,
    MarketDataUnreliable = 8,
    ExecutionUnavailable = 9,
    InvalidStop = 10,
    PositionTooSmall = 11,
}

/// <summary>Account state when an entry is evaluated (mark-to-market).</summary>
/// <param name="Equity">Cash plus the value of open positions.</param>
/// <param name="Cash">Quote currency available to spend.</param>
/// <param name="OpenPositions">Number of open positions.</param>
/// <param name="OpenExposure">Value of the open positions.</param>
public sealed record PortfolioSnapshot(decimal Equity, decimal Cash, int OpenPositions, decimal OpenExposure);

/// <summary>Market and system conditions when an entry is evaluated.</summary>
public sealed record MarketConditions(decimal SpreadBps, decimal ExpectedSlippageBps, bool MarketDataReliable, bool ExecutionAvailable);

/// <summary>Outcome of a risk evaluation. A rejection always names its control and explains it.</summary>
public sealed record RiskDecision(bool Approved, RiskCheck? Check, string Detail, decimal Quantity)
{
    public static RiskDecision Approve(string detail, decimal quantity = 0m) => new(true, null, detail, quantity);

    public static RiskDecision Reject(RiskCheck check, string detail) => new(false, check, detail, 0m);

    /// <summary>The NO_TRADE reason (CLAUDE.md §8) that corresponds to a rejection.</summary>
    public NoTradeReason? NoTradeReason => Check switch
    {
        null => null,
        RiskCheck.ExcessiveSpread => Core.Trading.NoTradeReason.ExcessiveSpread,
        RiskCheck.ExcessiveSlippage => Core.Trading.NoTradeReason.ExcessiveSlippage,
        RiskCheck.MarketDataUnreliable => Core.Trading.NoTradeReason.InvalidMarketData,
        RiskCheck.ExecutionUnavailable => Core.Trading.NoTradeReason.ExecutionUnavailable,
        _ => Core.Trading.NoTradeReason.RiskLimitReached,
    };

    /// <summary>Stable code for journals and results, e.g. RISK_DAILY_LOSS_LIMIT.</summary>
    public string? Code => Check is { } check ? "RISK_" + string.Concat(check.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant() : null;
}
