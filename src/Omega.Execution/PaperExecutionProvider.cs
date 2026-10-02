using System.Globalization;
using Omega.Core.MarketData;
using Omega.Core.Trading;

namespace Omega.Execution;

/// <summary>
/// Simulated execution for paper trading (Phase 12). Market orders fill at their reference price with market costs;
/// a filled buy places its bracket (stop-market + limit, one-cancels-the-other), triggered later from closed candles
/// with <see cref="CandleFillModel"/>, exactly as in the backtester. Every order goes through the full lifecycle.
/// </summary>
public sealed class PaperExecutionProvider(TradingCosts costs) : IExecutionProvider
{
    private readonly List<Order> _working = [];
    private long _sequence;

    public IReadOnlyList<Order> WorkingOrders => _working;

    /// <summary>Restores the orders still working after a restart.</summary>
    public void Restore(IEnumerable<Order> workingOrders, long sequence)
    {
        ArgumentNullException.ThrowIfNull(workingOrders);
        _working.Clear();
        _working.AddRange(workingOrders.Where(o => o.IsWorking));
        _sequence = sequence;
    }

    /// <summary>Last client-order sequence number used (persisted with the session).</summary>
    public long Sequence => _sequence;

    public Task<OrderResult> PlaceMarketOrderAsync(MarketOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var order = New(request.Side, OrderType.Market, request.Quantity, null, null, null, null, request.TimeUtc);
        order.Transition(OrderStatus.Submitted, request.TimeUtc, request.Note);
        order.Transition(OrderStatus.Acknowledged, request.TimeUtc);

        var price = request.Side == OrderSide.Buy
            ? CandleFillModel.MarketBuyPrice(request.ReferencePrice, costs)
            : CandleFillModel.MarketSellPrice(request.ReferencePrice, costs);
        order.Fill(request.Quantity, price, request.Quantity * price * costs.FeeRate, request.TimeUtc);

        var created = new List<Order>();
        if (request.Side == OrderSide.Buy && request.Bracket is { } bracket)
        {
            var group = Guid.NewGuid();
            created.Add(Work(New(OrderSide.Sell, OrderType.StopMarket, request.Quantity, bracket.StopLoss, null, order.Id, group, request.TimeUtc), request.TimeUtc));
            if (bracket.TakeProfit is { } target)
            {
                created.Add(Work(New(OrderSide.Sell, OrderType.Limit, request.Quantity, null, target, order.Id, group, request.TimeUtc), request.TimeUtc));
            }
        }

        return Task.FromResult(new OrderResult(order, created));
    }

    public Task<Order> CancelOrderAsync(Guid orderId, DateTimeOffset timeUtc, string reason, CancellationToken cancellationToken)
    {
        var order = _working.FirstOrDefault(o => o.Id == orderId)
            ?? throw new InvalidOperationException($"Order {orderId} is not working.");
        order.Transition(OrderStatus.Canceled, timeUtc, reason);
        _working.Remove(order);
        return Task.FromResult(order);
    }

    public Task<IReadOnlyList<ProtectionReport>> SynchronizeAsync(Candle closedCandle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(closedCandle);

        var reports = new List<ProtectionReport>();
        foreach (var group in _working.Where(o => o.OcoGroup is not null).GroupBy(o => o.OcoGroup).ToList())
        {
            var stop = group.Single(o => o.Type == OrderType.StopMarket);
            var target = group.SingleOrDefault(o => o.Type == OrderType.Limit);
            if (CandleFillModel.CheckProtection(closedCandle, stop.StopPrice!.Value, target?.LimitPrice, costs) is not { } fill)
            {
                continue;
            }

            var (filled, other) = fill.Reason == ExitReason.StopLoss ? (stop, target) : (target!, stop);
            filled.Fill(filled.Quantity, fill.Price, filled.Quantity * fill.Price * costs.FeeRate, closedCandle.OpenTimeUtc);
            _working.Remove(filled);
            if (other is not null)
            {
                other.Transition(OrderStatus.Canceled, closedCandle.OpenTimeUtc, "One-cancels-the-other: sibling filled.");
                _working.Remove(other);
            }

            reports.Add(new ProtectionReport(filled, other, fill.Reason));
        }

        return Task.FromResult<IReadOnlyList<ProtectionReport>>(reports);
    }

    private Order Work(Order order, DateTimeOffset time)
    {
        order.Transition(OrderStatus.Submitted, time);
        order.Transition(OrderStatus.Acknowledged, time);
        _working.Add(order);
        return order;
    }

    private Order New(OrderSide side, OrderType type, decimal quantity, decimal? stop, decimal? limit, Guid? parent, Guid? group, DateTimeOffset time) =>
        new(Guid.NewGuid(), "omega-paper-" + (++_sequence).ToString("D8", CultureInfo.InvariantCulture), side, type, quantity, stop, limit, parent, group, time);
}
