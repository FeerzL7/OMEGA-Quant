using Omega.Core.MarketData;

namespace Omega.Execution;

/// <summary>Protective orders attached to an entry: placed when the entry fills, one cancels the other.</summary>
public sealed record Bracket(decimal StopLoss, decimal? TakeProfit);

/// <summary>What to execute.</summary>
/// <param name="Side">Buy or sell.</param>
/// <param name="Quantity">Base-asset quantity, already approved and sized by the Risk Engine.</param>
/// <param name="ReferencePrice">Market price the order acts at (paper: the fill reference; live: the last known price).</param>
/// <param name="TimeUtc">When the order is sent.</param>
/// <param name="Bracket">Stop loss and take profit to protect a buy once it fills.</param>
/// <param name="Note">Why the order exists (journal).</param>
public sealed record MarketOrderRequest(OrderSide Side, decimal Quantity, decimal ReferencePrice, DateTimeOffset TimeUtc, Bracket? Bracket = null, string? Note = null);

/// <summary>Result of sending an order: the order as the venue left it and the orders it created (protection).</summary>
public sealed record OrderResult(Order Order, IReadOnlyList<Order> CreatedOrders);

/// <summary>A protective order that filled while synchronizing with a candle.</summary>
public sealed record ProtectionReport(Order Filled, Order? Canceled, ExitReason Reason);

/// <summary>
/// Executes orders (CLAUDE.md §16). Strategies never see which implementation runs: backtest, paper, testnet or live.
/// </summary>
public interface IExecutionProvider
{
    Task<OrderResult> PlaceMarketOrderAsync(MarketOrderRequest request, CancellationToken cancellationToken);

    Task<Order> CancelOrderAsync(Guid orderId, DateTimeOffset timeUtc, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Brings working orders up to date as of a closed candle: the simulator triggers protective orders from the
    /// candle; a live provider reconciles them with the exchange (an uncertain result is never assumed, §15).
    /// </summary>
    Task<IReadOnlyList<ProtectionReport>> SynchronizeAsync(Candle closedCandle, CancellationToken cancellationToken);

    /// <summary>Orders that are still working (not terminal).</summary>
    IReadOnlyList<Order> WorkingOrders { get; }
}
