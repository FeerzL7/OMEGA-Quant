using Omega.Core.Trading;

namespace Omega.Execution.Paper;

/// <summary>A closed paper position (round trip). Prices are fill prices; PnL is net of fees.</summary>
public sealed record PaperTrade(
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
    string? EntryExplanation);

/// <summary>What the pipeline did with one closed candle (journal entry, CLAUDE.md §27).</summary>
/// <param name="CandleOpenTimeUtc">Decision candle.</param>
/// <param name="Direction">Strategy signal.</param>
/// <param name="NoTradeReason">Reason when the signal was NO_TRADE.</param>
/// <param name="Explanation">Strategy explanation.</param>
/// <param name="Metrics">Strategy numbers (raw and calibrated probability, expected value...).</param>
/// <param name="Action">ENTRY_SCHEDULED, EXIT_SCHEDULED, RISK_REJECTED, NO_TRADE, HOLD or IGNORED.</param>
/// <param name="RiskCode">Risk rejection code, if any.</param>
/// <param name="RiskDetail">Risk rejection detail, if any.</param>
/// <param name="Equity">Equity marked at the candle close.</param>
/// <param name="Notes">Execution events of this candle (fills, rejections, expirations).</param>
public sealed record PaperDecision(
    DateTimeOffset CandleOpenTimeUtc,
    SignalDirection Direction,
    NoTradeReason? NoTradeReason,
    string? Explanation,
    IReadOnlyDictionary<string, double> Metrics,
    string Action,
    string? RiskCode,
    string? RiskDetail,
    decimal Equity,
    IReadOnlyList<string> Notes);

/// <summary>Everything one processed candle changed; persisted in a single transaction.</summary>
public sealed record PaperStep(
    Guid SessionId,
    DateTimeOffset CandleOpenTimeUtc,
    string StateJson,
    IReadOnlyList<Order> Orders,
    IReadOnlyList<PaperTrade> Trades,
    PaperDecision Decision);

/// <summary>A paper trading session: its fixed configuration and its evolving state (both JSON, owned by the engine).</summary>
public sealed record PaperSessionRecord(
    Guid Id,
    string Name,
    string Symbol,
    string Interval,
    string ConfigJson,
    string StateJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastCandleOpenTimeUtc);

public enum PaperCommandType
{
    TripKillSwitch = 1,
    ResetKillSwitch = 2,
}

/// <summary>An operator command sent through the API and applied by the worker (audited).</summary>
public sealed record PaperCommand(
    long Id,
    Guid SessionId,
    PaperCommandType Type,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? AppliedAtUtc,
    string? Result);

/// <summary>Durable state of paper trading. Implementations throw <c>PersistenceException</c> on failures.</summary>
public interface IPaperTradingStore
{
    Task<PaperSessionRecord?> GetSessionAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaperSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken);

    Task CreateSessionAsync(PaperSessionRecord session, CancellationToken cancellationToken);

    /// <summary>Persists one processed candle atomically: orders (upsert) and their new events, trades, journal entry, state and cursor.</summary>
    Task SaveStepAsync(PaperStep step, CancellationToken cancellationToken);

    Task<IReadOnlyList<Order>> GetWorkingOrdersAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<long> EnqueueCommandAsync(Guid sessionId, PaperCommandType type, string reason, string requestedBy, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaperCommand>> GetPendingCommandsAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Marks a command applied and saves the state it produced, atomically.</summary>
    Task CompleteCommandAsync(long commandId, Guid sessionId, string result, string stateJson, DateTimeOffset appliedAtUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaperCommand>> GetCommandsAsync(Guid sessionId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Order>> GetOrdersAsync(Guid sessionId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaperTrade>> GetTradesAsync(Guid sessionId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaperDecision>> GetDecisionsAsync(Guid sessionId, int limit, CancellationToken cancellationToken);
}
