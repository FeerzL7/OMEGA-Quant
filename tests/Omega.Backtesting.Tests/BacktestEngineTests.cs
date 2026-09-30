using Omega.Backtesting.Tests.TestSupport;
using Omega.Core.Trading;
using static Omega.Backtesting.Tests.TestSupport.Bars;

namespace Omega.Backtesting.Tests;

public class BacktestEngineTests
{
    [Fact]
    public void Entry_happens_at_the_next_open_never_at_the_signal_close()
    {
        // Signal candle closes at 100; the entry candle opens at 105 and closes at 108: all three prices differ,
        // so filling at the signal close or at the entry candle's close (both look-ahead) would be detected.
        var candles = new[] { Flat(0, 100m), At(1, 105m, 109m, 104m, 108m), Flat(2, 108m) };
        var strategy = new ScriptedStrategy(new() { [0] = Signal.Long(90m) });

        var trade = Assert.Single(Run(candles, strategy).Trades);

        Assert.Equal(OpenTime(1), trade.EntryCandleOpenTimeUtc);
        Assert.Equal(105m, trade.EntryPrice);
    }

    [Fact]
    public void Signal_exit_happens_at_the_next_open_never_at_a_close()
    {
        var candles = new[] { Flat(0, 100m), At(1, 100m, 104m, 99m, 103m), At(2, 106m, 107m, 101m, 102m), Flat(3, 102m) };
        var strategy = new ScriptedStrategy(new() { [0] = Signal.Long(90m), [1] = Signal.Short() });

        var trade = Assert.Single(Run(candles, strategy).Trades);

        Assert.Equal((ExitReason.Signal, 106m, OpenTime(2)), (trade.ExitReason, trade.ExitPrice, trade.ExitCandleOpenTimeUtc));
    }

    [Fact]
    public void Stop_loss_touched_during_a_candle_exits_at_the_stop()
    {
        var candles = new[] { Flat(0, 100m), Flat(1, 100m), At(2, 100m, 101m, 89m, 95m), Flat(3, 95m) };

        var trade = Assert.Single(Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) })).Trades);

        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(90m, trade.ExitPrice);
        Assert.Equal(OpenTime(2), trade.ExitCandleOpenTimeUtc);
        Assert.Equal(trade.Quantity * (90m - 100m), trade.NetPnl);
    }

    [Fact]
    public void When_stop_and_target_are_both_touched_the_stop_is_assumed_first()
    {
        var candles = new[] { Flat(0, 100m), Flat(1, 100m), At(2, 100m, 115m, 85m, 100m) };

        var trade = Assert.Single(Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m, 110m) })).Trades);

        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(90m, trade.ExitPrice);
    }

    [Fact]
    public void Opening_below_the_stop_fills_at_the_open()
    {
        var candles = new[] { Flat(0, 100m), Flat(1, 100m), At(2, 85m, 86m, 80m, 82m) };

        var trade = Assert.Single(Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) })).Trades);

        Assert.Equal(85m, trade.ExitPrice);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
    }

    [Fact]
    public void Take_profit_fills_at_its_price_even_on_a_gap_up()
    {
        var touched = new[] { Flat(0, 100m), Flat(1, 100m), At(2, 100m, 112m, 99m, 104m) };
        var gapped = new[] { Flat(0, 100m), Flat(1, 100m), At(2, 120m, 125m, 119m, 121m) };
        var strategy = () => new ScriptedStrategy(new() { [0] = Signal.Long(90m, 110m) });

        var normal = Assert.Single(Run(touched, strategy()).Trades);
        var gap = Assert.Single(Run(gapped, strategy()).Trades);

        Assert.Equal((ExitReason.TakeProfit, 110m), (normal.ExitReason, normal.ExitPrice));
        Assert.Equal((ExitReason.TakeProfit, 110m), (gap.ExitReason, gap.ExitPrice));
    }

    [Fact]
    public void Short_signal_closes_a_long_at_the_next_open_and_never_opens_a_short()
    {
        var candles = Flats(100m, 100m, 104m, 107m, 107m);
        var strategy = new ScriptedStrategy(new() { [0] = Signal.Long(90m), [2] = Signal.Short(), [3] = Signal.Short() });

        var result = Run(candles, strategy);

        var trade = Assert.Single(result.Trades);
        Assert.Equal((ExitReason.Signal, 107m, OpenTime(3)), (trade.ExitReason, trade.ExitPrice, trade.ExitCandleOpenTimeUtc));
        Assert.Equal(RejectionCodes.ShortNotSupportedOnSpot, Assert.Single(result.Rejections).Code);
    }

    [Fact]
    public void Open_position_is_closed_at_the_last_close_and_the_last_candle_is_not_evaluated()
    {
        var candles = Flats(100m, 100m, 101m, 102m);
        var strategy = new ScriptedStrategy(new() { [0] = Signal.Long(90m) });

        var result = Run(candles, strategy);

        var trade = Assert.Single(result.Trades);
        Assert.Equal((ExitReason.EndOfData, 102m), (trade.ExitReason, trade.ExitPrice));
        Assert.Equal(3, strategy.Seen.Count);
    }

    [Fact]
    public void Time_limit_closes_at_the_close_of_the_last_allowed_candle()
    {
        var candles = Flats(100m, 100m, 101m, 102m, 103m);
        var engine = Engines.Create(Engines.NoCosts with { MaxHoldingCandles = 2 });

        var trade = Assert.Single(engine.Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) })).Value.Trades);

        Assert.Equal((ExitReason.TimeLimit, 101m, 2), (trade.ExitReason, trade.ExitPrice, trade.HoldingCandles));
    }

    [Fact]
    public void Entry_is_rejected_when_the_market_opens_at_or_below_the_stop()
    {
        var candles = new[] { Flat(0, 100m), At(1, 89m, 92m, 88m, 90m), Flat(2, 90m) };

        var result = Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) }));

        Assert.Empty(result.Trades);
        Assert.Equal(RejectionCodes.StopNotBelowEntry, Assert.Single(result.Rejections).Code);
    }

    [Fact]
    public void Position_is_sized_by_risk_and_capped_by_capital()
    {
        var candles = Flats(100m, 100m, 100m);

        var byRisk = Assert.Single(Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) })).Trades);
        var capped = Assert.Single(Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(99.9m) })).Trades);

        Assert.Equal(10m, byRisk.Quantity);          // 10 000 × 1 % / (100 − 90)
        Assert.Equal(100m, capped.Quantity);         // risk would allow 1 000; capital allows 10 000 / 100
    }

    [Fact]
    public void Costs_are_applied_exactly_on_both_sides()
    {
        var config = new BacktestConfig { FeeRate = 0.001m, SpreadBps = 2m, SlippageBps = 3m };
        var candles = Flats(100m, 100m, 110m, 110m);
        var engine = Engines.Create(config);

        var result = engine.Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m), [1] = Signal.Short() })).Value;
        var trade = Assert.Single(result.Trades);

        var entryPrice = 100m * (1m + 0.0001m + 0.0003m);        // half spread + slippage against us
        var exitPrice = 110m * (1m - 0.0001m - 0.0003m);
        var quantity = 10_000m * 0.01m / (entryPrice - 90m);
        var entryFee = quantity * entryPrice * 0.001m;
        var exitFee = quantity * exitPrice * 0.001m;

        Assert.Equal(entryPrice, trade.EntryPrice);
        Assert.Equal(exitPrice, trade.ExitPrice);
        Assert.Equal(quantity, trade.Quantity);
        Assert.Equal((quantity * exitPrice) - exitFee - ((quantity * entryPrice) + entryFee), trade.NetPnl);
        Assert.Equal(10_000m + trade.NetPnl, result.Metrics.FinalEquity);
        Assert.Equal(entryFee + exitFee, result.Metrics.TotalFees);
    }

    [Fact]
    public void Quantity_step_and_minimum_notional_are_respected()
    {
        var candles = Flats(100m, 100m, 100m);
        var stepped = Engines.Create(Engines.NoCosts with { QuantityStep = 0.001m });
        var tooSmall = Engines.Create(Engines.NoCosts with { MinNotional = 5_000m });

        var steppedTrade = Assert.Single(stepped.Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(97m) })).Value.Trades);
        var rejected = tooSmall.Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) })).Value;

        Assert.Equal(33.333m, steppedTrade.Quantity);   // 100 / 3 = 33.333…, rounded down
        Assert.Equal(RejectionCodes.PositionTooSmall, Assert.Single(rejected.Rejections).Code);
    }

    [Fact]
    public void Equity_is_marked_at_every_close_with_its_drawdown()
    {
        var candles = Flats(100m, 100m, 110m, 99m, 104.5m);
        var result = Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) }));

        // 10 units bought at 100 at the open of candle 1.
        Assert.Equal([10_000m, 10_000m, 10_100m, 9_990m, 10_045m], result.EquityCurve.Select(p => p.Equity));
        Assert.Equal(9_990m / 10_100m - 1m, result.EquityCurve[3].Drawdown);
        Assert.Equal((double)(9_990m / 10_100m - 1m), result.Metrics.MaxDrawdown);
        Assert.Equal(0.8, result.Metrics.Exposure, 10);     // candles 1-4 (the last one exits at its close)
    }

    [Fact]
    public void Trade_statistics_are_computed_from_net_results()
    {
        // Trade 1: +100 (100 → 110, 10 units). Trade 2: −50.5 (110 → 105, 10.1 units: 1 % of 10 100 / stop distance 10).
        var candles = Flats(100m, 100m, 110m, 110m, 110m, 105m, 105m);
        var strategy = new ScriptedStrategy(new() { [0] = Signal.Long(90m), [2] = Signal.Short(), [3] = Signal.Long(100m), [5] = Signal.Short() });

        var metrics = Run(candles, strategy).Metrics;

        Assert.Equal(2, metrics.TradeCount);
        Assert.Equal(0.5, metrics.WinRate);
        Assert.Equal(100m, metrics.GrossProfit);
        Assert.Equal(-50.5m, metrics.GrossLoss);
        Assert.Equal(24.75m, metrics.Expectancy);          // (100 − 50.5) / 2
        Assert.Equal((double)(100m / 50.5m), metrics.ProfitFactor!.Value, 12);
    }

    [Fact]
    public void Signal_pending_across_a_data_gap_expires()
    {
        var candles = new[] { Flat(0, 100m), Flat(2, 100m), Flat(3, 100m) };

        var result = Run(candles, new ScriptedStrategy(new() { [0] = Signal.Long(90m) }));

        Assert.Empty(result.Trades);
        Assert.Equal(RejectionCodes.SignalExpiredByDataGap, Assert.Single(result.Rejections).Code);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Strategy_is_not_consulted_until_features_are_complete()
    {
        var candles = Flats(100m, 100m, 100m, 100m, 100m, 100m);
        var strategy = new ScriptedStrategy();

        var result = Engines.Create(featureLookback: 3).Run(candles, strategy).Value;

        Assert.Equal(OpenTime(2), strategy.Seen[0].Candle.OpenTimeUtc);
        Assert.Equal(2, result.NoTradeCounts[NoTradeReason.FeaturesUnavailable]);
    }

    [Fact]
    public void Strategy_only_ever_sees_the_candle_that_just_closed_and_its_features()
    {
        var candles = Flats(100m, 101m, 102m, 103m);
        var strategy = new ScriptedStrategy();

        Run(candles, strategy);

        Assert.All(strategy.Seen, context =>
        {
            Assert.Equal(context.Candle.OpenTimeUtc, context.Features.OpenTimeUtc);
            Assert.Equal((double)context.Candle.Close, context.Features.Values["close"]);
        });
    }

    [Fact]
    public void Future_candles_cannot_change_past_decisions_or_trades()
    {
        var baseCandles = Enumerable.Range(0, 60).Select(i => Flat(i, 100m + (i % 7) - (i % 3))).ToArray();
        var altered = (Omega.Core.MarketData.Candle[])baseCandles.Clone();
        for (var i = 40; i < altered.Length; i++)
        {
            altered[i] = Flat(i, 500m);
        }

        var original = Engines.Create().Run(baseCandles, new MomentumStrategy()).Value;
        var withOtherFuture = Engines.Create().Run(altered, new MomentumStrategy()).Value;

        var closedBefore = (Omega.Backtesting.BacktestResult r) => r.Trades.Where(t => t.ExitCandleOpenTimeUtc < OpenTime(40)).ToList();
        Assert.NotEmpty(closedBefore(original));
        Assert.Equal(closedBefore(original), closedBefore(withOtherFuture));
        Assert.Equal(original.EquityCurve.Take(39), withOtherFuture.EquityCurve.Take(39));
    }

    [Fact]
    public void Runs_are_deterministic_and_identify_their_dataset()
    {
        var candles = Enumerable.Range(0, 80).Select(i => Flat(i, 100m + (i % 5))).ToArray();
        var changed = (Omega.Core.MarketData.Candle[])candles.Clone();
        changed[50] = Flat(50, 999m);

        var first = Engines.Create().Run(candles, new MomentumStrategy()).Value;
        var second = Engines.Create().Run(candles, new MomentumStrategy()).Value;
        var other = Engines.Create().Run(changed, new MomentumStrategy()).Value;

        Assert.Equal(first.Trades, second.Trades);
        Assert.Equal(first.Metrics, second.Metrics);
        Assert.Equal(first.Dataset, second.Dataset);
        Assert.NotEqual(first.Dataset.Sha256, other.Dataset.Sha256);
        Assert.Equal(80, first.Dataset.CandleCount);
        Assert.Equal("test-v1", first.FeatureSetVersion);
    }

    [Fact]
    public void Dataset_hash_ignores_trailing_zeros()
    {
        var a = new[] { At(0, 100m, 101m, 99m, 100.5m) };
        var b = new[] { At(0, 100.00m, 101.000m, 99.0m, 100.50m) };

        Assert.Equal(DatasetFingerprint.From(a).Sha256, DatasetFingerprint.From(b).Sha256);
    }

    [Fact]
    public void Invalid_configuration_is_rejected()
    {
        var engine = Engines.Create(new BacktestConfig { MaxPositionFraction = 2m });

        var result = engine.Run(Flats(100m, 100m), new ScriptedStrategy());

        Assert.Equal(BacktestErrors.InvalidConfig, result.Error!.Code);
    }

    private static BacktestResult Run(Omega.Core.MarketData.Candle[] candles, ScriptedStrategy strategy) =>
        Engines.Create().Run(candles, strategy).Value;
}
