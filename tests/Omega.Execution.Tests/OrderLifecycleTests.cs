using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Execution;

namespace Omega.Execution.Tests;

public class OrderLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_market_order_goes_created_submitted_acknowledged_filled()
    {
        var order = Market();

        order.Transition(OrderStatus.Submitted, T0);
        order.Transition(OrderStatus.Acknowledged, T0);
        order.Fill(2m, 100m, 0.2m, T0);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal([OrderStatus.Created, OrderStatus.Submitted, OrderStatus.Acknowledged, OrderStatus.Filled], order.Events.Select(e => e.Status));
        Assert.True(order.IsTerminal);
    }

    [Theory]
    [InlineData(OrderStatus.Created, OrderStatus.Filled)]          // never filled without being sent
    [InlineData(OrderStatus.Created, OrderStatus.Acknowledged)]
    [InlineData(OrderStatus.Submitted, OrderStatus.Filled)]        // not before the venue acknowledges it
    [InlineData(OrderStatus.Submitted, OrderStatus.Canceled)]      // an unacknowledged order is uncertain: reconcile, do not assume (§15)
    public void Impossible_transitions_are_refused(OrderStatus from, OrderStatus to)
    {
        var order = Market();
        if (from >= OrderStatus.Submitted) order.Transition(OrderStatus.Submitted, T0);

        Assert.Throws<InvalidOperationException>(() => order.Transition(to, T0));
    }

    [Theory]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.Canceled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Expired)]
    public void Terminal_states_are_final(OrderStatus terminal)
    {
        var order = Market();
        if (terminal == OrderStatus.Rejected)
        {
            order.Transition(OrderStatus.Rejected, T0, "insufficient balance");
        }
        else
        {
            order.Transition(OrderStatus.Submitted, T0);
            order.Transition(OrderStatus.Acknowledged, T0);
            if (terminal == OrderStatus.Filled) order.Fill(2m, 100m, 0m, T0);
            else order.Transition(terminal, T0, "test");
        }

        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.Acknowledged, T0));
        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.Canceled, T0));
    }

    [Fact]
    public void Partial_fills_accumulate_quantity_fees_and_the_average_price()
    {
        var order = Market();
        order.Transition(OrderStatus.Submitted, T0);
        order.Transition(OrderStatus.Acknowledged, T0);

        order.Fill(0.5m, 100m, 0.05m, T0);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        order.Fill(1.5m, 104m, 0.156m, T0.AddSeconds(1));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(2m, order.FilledQuantity);
        Assert.Equal(103m, order.AverageFillPrice);
        Assert.Equal(0.206m, order.Fee);
        Assert.Throws<InvalidOperationException>(() => order.Fill(0.1m, 100m, 0m, T0));
    }

    [Fact]
    public void Overfilling_is_refused()
    {
        var order = Market();
        order.Transition(OrderStatus.Submitted, T0);
        order.Transition(OrderStatus.Acknowledged, T0);

        Assert.Throws<InvalidOperationException>(() => order.Fill(3m, 100m, 0m, T0));
    }

    [Fact]
    public void Stop_and_limit_orders_need_their_prices()
    {
        Assert.Throws<ArgumentException>(() => new Order(Guid.NewGuid(), "x", OrderSide.Sell, OrderType.StopMarket, 1m, null, null, null, null, T0));
        Assert.Throws<ArgumentException>(() => new Order(Guid.NewGuid(), "x", OrderSide.Sell, OrderType.Limit, 1m, null, null, null, null, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Order(Guid.NewGuid(), "x", OrderSide.Buy, OrderType.Market, 0m, null, null, null, null, T0));
    }

    private static Order Market() => new(Guid.NewGuid(), "omega-test-1", OrderSide.Buy, OrderType.Market, 2m, null, null, null, null, T0);
}

public class PaperExecutionProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TradingCosts Costs = new(0.001m, 2m, 3m);

    [Fact]
    public async Task A_market_buy_fills_with_market_costs_and_places_its_bracket()
    {
        var provider = new PaperExecutionProvider(Costs);

        var result = await provider.PlaceMarketOrderAsync(new MarketOrderRequest(OrderSide.Buy, 2m, 100m, T0, new Bracket(95m, 110m)), CancellationToken.None);

        Assert.Equal(100m * (1m + 0.0001m + 0.0003m), result.Order.AverageFillPrice);
        Assert.Equal(2m * result.Order.AverageFillPrice!.Value * 0.001m, result.Order.Fee);
        Assert.Equal(2, result.CreatedOrders.Count);
        Assert.Equal(result.CreatedOrders[0].OcoGroup, result.CreatedOrders[1].OcoGroup);
        Assert.All(result.CreatedOrders, o => Assert.Equal((OrderStatus.Acknowledged, result.Order.Id), (o.Status, o.ParentOrderId!.Value)));
        Assert.Equal(2, provider.WorkingOrders.Count);
        Assert.Equal(["omega-paper-00000001", "omega-paper-00000002", "omega-paper-00000003"],
            new[] { result.Order }.Concat(result.CreatedOrders).Select(o => o.ClientOrderId));
    }

    [Fact]
    public async Task One_protective_order_filling_cancels_the_other()
    {
        var provider = new PaperExecutionProvider(Costs);
        await provider.PlaceMarketOrderAsync(new MarketOrderRequest(OrderSide.Buy, 2m, 100m, T0, new Bracket(95m, 110m)), CancellationToken.None);

        var quiet = await provider.SynchronizeAsync(Candle(100m, 104m, 97m, 103m), CancellationToken.None);
        var hit = await provider.SynchronizeAsync(Candle(103m, 111m, 102m, 109m), CancellationToken.None);

        Assert.Empty(quiet);
        var report = Assert.Single(hit);
        Assert.Equal((ExitReason.TakeProfit, 110m, OrderStatus.Filled), (report.Reason, report.Filled.AverageFillPrice!.Value, report.Filled.Status));
        Assert.Equal(OrderStatus.Canceled, report.Canceled!.Status);
        Assert.Empty(provider.WorkingOrders);
    }

    [Fact]
    public void The_fill_model_is_conservative_inside_a_candle()
    {
        // Both levels inside the candle: the stop is assumed first.
        Assert.Equal(ExitReason.StopLoss, CandleFillModel.CheckProtection(Candle(100m, 112m, 94m, 100m), 95m, 110m, Costs)!.Reason);
        // Gap through the stop: market sell at the open, not at the stop.
        Assert.Equal(CandleFillModel.MarketSellPrice(90m, Costs), CandleFillModel.CheckProtection(Candle(90m, 91m, 89m, 90m), 95m, 110m, Costs)!.Price);
        // Gap through the target: filled at the target (no credit for the gap).
        Assert.Equal(110m, CandleFillModel.CheckProtection(Candle(115m, 116m, 114m, 115m), 95m, 110m, Costs)!.Price);
        Assert.Null(CandleFillModel.CheckProtection(Candle(100m, 105m, 96m, 101m), 95m, 110m, Costs));
    }

    [Fact]
    public async Task Working_orders_and_the_sequence_survive_a_restart()
    {
        var first = new PaperExecutionProvider(Costs);
        await first.PlaceMarketOrderAsync(new MarketOrderRequest(OrderSide.Buy, 2m, 100m, T0, new Bracket(95m, 110m)), CancellationToken.None);

        var second = new PaperExecutionProvider(Costs);
        second.Restore(first.WorkingOrders, first.Sequence);
        var report = Assert.Single(await second.SynchronizeAsync(Candle(100m, 101m, 94m, 96m), CancellationToken.None));
        var next = await second.PlaceMarketOrderAsync(new MarketOrderRequest(OrderSide.Buy, 1m, 96m, T0), CancellationToken.None);

        Assert.Equal(ExitReason.StopLoss, report.Reason);
        Assert.Equal("omega-paper-00000004", next.Order.ClientOrderId);   // no client order id is ever reused
    }

    [Fact]
    public async Task Canceling_an_order_that_is_not_working_is_an_error()
    {
        var provider = new PaperExecutionProvider(Costs);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CancelOrderAsync(Guid.NewGuid(), T0, "test", CancellationToken.None));
    }

    private static Candle Candle(decimal open, decimal high, decimal low, decimal close) =>
        Omega.Core.MarketData.Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, T0, T0.AddMinutes(5).AddMilliseconds(-1), open, high, low, close, 1m, 1m, 1).Value;
}
