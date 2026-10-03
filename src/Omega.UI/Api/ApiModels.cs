namespace Omega.UI.Api;

// The dashboard's own view of the API contract. Omega.UI references no project on purpose (docs/ARCHITECTURE.md §5);
// contract tests deserialize samples produced from the real API types into these records, so a change in the API
// breaks a test instead of silently breaking a panel. Enums travel as their names and are kept as strings here.

public sealed record SystemStatusDto(string Service, string TradingMode, DateTimeOffset TimestampUtc);

public sealed record CandleDto(
    DateTimeOffset OpenTimeUtc, DateTimeOffset CloseTimeUtc, decimal Open, decimal High, decimal Low, decimal Close,
    decimal BaseVolume, decimal QuoteVolume, long TradeCount);

public sealed record MarketStateDto(
    string Symbol, string Interval, DateTimeOffset EvaluatedAtUtc, bool IsReliable, IReadOnlyList<string> Issues, string Freshness,
    double? DataAgeSeconds, DateTimeOffset? NextCloseExpectedUtc, CandleDto? LastClosedCandle);

public sealed record ComponentHealthDto(string Name, string State, string Detail, string Basis);

public sealed record SystemHealthDto(DateTimeOffset CheckedAtUtc, IReadOnlyList<ComponentHealthDto> Components);

public sealed record SystemEventDto(DateTimeOffset OccurredAtUtc, string Source, string EventType, string Severity, string Message);

public sealed record RiskLimitsDto(
    decimal RiskPerTrade, decimal MaxPositionFraction, int MaxOpenPositions, decimal MaxExposureFraction, decimal MaxDailyLoss,
    decimal MaxDrawdown, int? MaxConsecutiveLosses, decimal MaxSpreadBps, decimal MaxSlippageBps);

public sealed record PaperPositionDto(DateTimeOffset EntryCandleOpenTimeUtc, decimal EntryPrice, decimal Quantity, decimal StopLoss, decimal? TakeProfit, int HoldingCandles);

public sealed record PaperKillSwitchDto(bool Active, string? Reason, DateTimeOffset? TrippedAtUtc, string? ResetBy);

public sealed record PaperSessionDto(
    string Name, string Symbol, string Interval, string Strategy, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastCandleOpenTimeUtc, int CandlesProcessed, decimal Cash, decimal LastEquity, decimal PeakEquity, decimal Drawdown,
    decimal DayStartEquity, int ConsecutiveLosses, PaperPositionDto? Position, bool EntryPending, bool ExitPending, PaperKillSwitchDto KillSwitch);

public sealed record PaperDecisionDto(
    DateTimeOffset CandleOpenTimeUtc, string Direction, string? NoTradeReason, string? Explanation, IReadOnlyDictionary<string, double> Metrics,
    string Action, string? RiskCode, string? RiskDetail, decimal Equity, IReadOnlyList<string> Notes);

public sealed record PaperTradeDto(
    DateTimeOffset EntryCandleOpenTimeUtc, decimal EntryPrice, decimal Quantity, decimal EntryFee, decimal StopLossPrice, decimal? TakeProfitPrice,
    DateTimeOffset ExitCandleOpenTimeUtc, decimal ExitPrice, decimal ExitFee, string ExitReason, int HoldingCandles, decimal NetPnl, string? EntryExplanation);

public sealed record OrderDto(
    Guid Id, string ClientOrderId, string Side, string Type, decimal Quantity, decimal? StopPrice, decimal? LimitPrice, string Status,
    decimal FilledQuantity, decimal? AverageFillPrice, decimal Fee, string? Reason, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record OrderEventDto(int Sequence, string Status, DateTimeOffset TimeUtc, string? Detail);

public sealed record PaperOrderViewDto(OrderDto Order, IReadOnlyList<OrderEventDto> Events);

public sealed record PaperCommandDto(long Id, string Type, string Reason, string RequestedBy, DateTimeOffset RequestedAtUtc, DateTimeOffset? AppliedAtUtc, string? Result);

public sealed record KillSwitchAcceptedDto(long CommandId, string Session, string Action, string Note);

public sealed record BacktestRunSummaryDto(
    Guid Id, DateTimeOffset CreatedAtUtc, string PeriodLabel, string StrategyName, string StrategyVersion, string Symbol, string Interval,
    DateTimeOffset TradingStartUtc, DateTimeOffset TradingEndUtc, string DatasetSha256, int TradeCount, double TotalReturn);

public sealed record EquityPointDto(DateTimeOffset TimeUtc, decimal Equity, decimal Drawdown);

public sealed record BacktestMetricsDto(
    decimal InitialCapital, decimal FinalEquity, decimal NetProfit, double TotalReturn, int TradeCount, int WinningTrades, int LosingTrades,
    double? WinRate, double? ProfitFactor, decimal? Expectancy, double MaxDrawdown, double? Sharpe, double? Sortino, decimal TotalFees, double Exposure);

public sealed record BacktestTradeDto(
    DateTimeOffset EntryCandleOpenTimeUtc, decimal EntryPrice, decimal Quantity, DateTimeOffset ExitCandleOpenTimeUtc, decimal ExitPrice,
    string ExitReason, int HoldingCandles, decimal NetPnl);

public sealed record StrategyIdentityDto(string Name, string Version, IReadOnlyDictionary<string, string> Parameters);

public sealed record BacktestResultDto(
    StrategyIdentityDto Strategy, string FeatureSetVersion, IReadOnlyList<BacktestTradeDto> Trades, IReadOnlyList<EquityPointDto> EquityCurve,
    BacktestMetricsDto Metrics, IReadOnlyList<string> Warnings);

public sealed record BacktestRunDto(
    Guid Id, DateTimeOffset CreatedAtUtc, string PeriodLabel, DateTimeOffset TradingStartUtc, DateTimeOffset TradingEndUtc,
    IReadOnlyList<string> Notices, BacktestResultDto Result);

public sealed record DistributionDto(double Mean, double Min, double P5, double P25, double P50, double P75, double P95, double Max);

public sealed record MonteCarloResultDto(
    int SourceTrades, int HorizonTrades, DistributionDto TerminalReturn, DistributionDto MaxDrawdownDepth, DistributionDto LongestLosingStreak,
    double ProbabilityOfLoss, double ProbabilityOfRuin, double? ProbabilityKillSwitch);

public sealed record MonteCarloOutcomeDto(Guid RunId, string Strategy, string PeriodLabel, double BacktestMaxDrawdownDepth, MonteCarloResultDto Result, IReadOnlyList<string> Notices);
