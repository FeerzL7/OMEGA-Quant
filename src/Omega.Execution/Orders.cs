namespace Omega.Execution;

public enum OrderSide
{
    Buy = 1,
    Sell = 2,
}

public enum OrderType
{
    Market = 1,
    StopMarket = 2,
    Limit = 3,
}

/// <summary>Order lifecycle (CLAUDE.md §15). An order is not a fill.</summary>
public enum OrderStatus
{
    Created = 1,
    Submitted = 2,
    Acknowledged = 3,
    PartiallyFilled = 4,
    Filled = 5,
    Rejected = 6,
    Canceled = 7,
    Expired = 8,
}

/// <summary>One recorded change of an order's status. <paramref name="Sequence"/> numbers the events of an order from 0, across restarts.</summary>
public sealed record OrderEvent(Guid OrderId, int Sequence, OrderStatus Status, DateTimeOffset TimeUtc, string? Detail);

/// <summary>
/// An order and its lifecycle. Every status change goes through <see cref="Transition"/>, which refuses impossible
/// changes (for example from Filled back to Acknowledged) and records an event.
/// </summary>
public sealed class Order
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Allowed = new()
    {
        [OrderStatus.Created] = [OrderStatus.Submitted, OrderStatus.Rejected],
        [OrderStatus.Submitted] = [OrderStatus.Acknowledged, OrderStatus.Rejected],
        [OrderStatus.Acknowledged] = [OrderStatus.PartiallyFilled, OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Expired],
        [OrderStatus.PartiallyFilled] = [OrderStatus.PartiallyFilled, OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Expired],
        [OrderStatus.Filled] = [],
        [OrderStatus.Rejected] = [],
        [OrderStatus.Canceled] = [],
        [OrderStatus.Expired] = [],
    };

    private readonly List<OrderEvent> _events = [];
    private int _nextSequence;

    public Order(
        Guid id, string clientOrderId, OrderSide side, OrderType type, decimal quantity, decimal? stopPrice, decimal? limitPrice,
        Guid? parentOrderId, Guid? ocoGroup, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0m);
        if (type == OrderType.StopMarket && stopPrice is not > 0) throw new ArgumentException("A stop-market order needs a positive stop price.", nameof(stopPrice));
        if (type == OrderType.Limit && limitPrice is not > 0) throw new ArgumentException("A limit order needs a positive limit price.", nameof(limitPrice));

        Id = id;
        ClientOrderId = clientOrderId;
        Side = side;
        Type = type;
        Quantity = quantity;
        StopPrice = stopPrice;
        LimitPrice = limitPrice;
        ParentOrderId = parentOrderId;
        OcoGroup = ocoGroup;
        CreatedAtUtc = UpdatedAtUtc = createdAtUtc;
        _events.Add(new OrderEvent(id, _nextSequence++, OrderStatus.Created, createdAtUtc, null));
    }

    public Guid Id { get; }

    public string ClientOrderId { get; }

    public OrderSide Side { get; }

    public OrderType Type { get; }

    public decimal Quantity { get; }

    public decimal? StopPrice { get; }

    public decimal? LimitPrice { get; }

    /// <summary>The entry order a protective order belongs to.</summary>
    public Guid? ParentOrderId { get; }

    /// <summary>Orders sharing a group cancel each other when one fills (one-cancels-the-other).</summary>
    public Guid? OcoGroup { get; }

    public OrderStatus Status { get; private set; } = OrderStatus.Created;

    public decimal FilledQuantity { get; private set; }

    public decimal? AverageFillPrice { get; private set; }

    public decimal Fee { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool IsTerminal => Status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Canceled or OrderStatus.Expired;

    public bool IsWorking => Status is OrderStatus.Acknowledged or OrderStatus.PartiallyFilled;

    /// <summary>Events recorded since the order was created or restored (persisted ones are not kept in memory).</summary>
    public IReadOnlyList<OrderEvent> Events => _events;

    public void Transition(OrderStatus status, DateTimeOffset timeUtc, string? detail = null)
    {
        if (!Allowed[Status].Contains(status))
        {
            throw new InvalidOperationException($"Order {ClientOrderId}: {Status} → {status} is not a valid transition.");
        }

        Status = status;
        UpdatedAtUtc = timeUtc;
        if (status is OrderStatus.Rejected or OrderStatus.Canceled or OrderStatus.Expired)
        {
            Reason = detail;
        }

        _events.Add(new OrderEvent(Id, _nextSequence++, status, timeUtc, detail));
    }

    /// <summary>Records a fill (complete or partial) at <paramref name="price"/> with its commission.</summary>
    public void Fill(decimal quantity, decimal price, decimal fee, DateTimeOffset timeUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(price, 0m);
        if (FilledQuantity + quantity > Quantity)
        {
            throw new InvalidOperationException($"Order {ClientOrderId}: fill of {quantity} exceeds the remaining {Quantity - FilledQuantity}.");
        }

        var filled = FilledQuantity + quantity;

        // First fill: the average is the price itself. Computing (price × quantity) / quantity would round in the
        // 28th digit and make fills differ from the backtester's by a hair, which then propagates into PnL.
        AverageFillPrice = FilledQuantity == 0m ? price : ((AverageFillPrice!.Value * FilledQuantity) + (price * quantity)) / filled;
        FilledQuantity = filled;
        Fee += fee;
        Transition(filled == Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled, timeUtc, $"{quantity} @ {price}");
    }

    /// <summary>
    /// Rebuilds an order from storage (paper trading restart). Persisted events are not kept in memory; new events
    /// continue the sequence after <paramref name="persistedEvents"/>, so none is lost or duplicated.
    /// </summary>
    public static Order Restore(
        Guid id, string clientOrderId, OrderSide side, OrderType type, decimal quantity, decimal? stopPrice, decimal? limitPrice,
        Guid? parentOrderId, Guid? ocoGroup, DateTimeOffset createdAtUtc, OrderStatus status, decimal filledQuantity,
        decimal? averageFillPrice, decimal fee, string? reason, DateTimeOffset updatedAtUtc, int persistedEvents)
    {
        var order = new Order(id, clientOrderId, side, type, quantity, stopPrice, limitPrice, parentOrderId, ocoGroup, createdAtUtc)
        {
            Status = status,
            FilledQuantity = filledQuantity,
            AverageFillPrice = averageFillPrice,
            Fee = fee,
            Reason = reason,
            UpdatedAtUtc = updatedAtUtc,
        };
        order._events.Clear();
        order._nextSequence = persistedEvents;
        return order;
    }
}
