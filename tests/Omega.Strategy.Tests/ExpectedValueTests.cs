using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Strategy.Calibration;
using Omega.Strategy.Models;

namespace Omega.Strategy.Tests;

public class ExpectedValueTests
{
    private static readonly OutcomeProfile Profile = new(2.9, -1.6);
    private static readonly TradingCosts Costs = new(0.001m, 2m, 3m);
    private static readonly DateTimeOffset Open = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Expected_value_follows_the_documented_formula()
    {
        var ev = ExpectedValueCalculator.Compute(0.4, atr: 200, price: 50_000, Profile, Costs);

        const double a = 200.0 / 50_000;
        const double market = 0.001 + 0.0001 + 0.0003;
        Assert.Equal(2.9 * a, ev.Gain, 15);
        Assert.Equal(-1.6 * a, ev.Loss, 15);
        Assert.Equal(market + (0.4 * 0.001) + (0.6 * market), ev.Costs, 15);
        Assert.Equal((0.4 * 2.9 * a) + (0.6 * -1.6 * a) - ev.Costs, ev.ExpectedReturn, 15);
    }

    [Fact]
    public void A_high_probability_is_not_enough_when_costs_exceed_the_edge()
    {
        // Tiny barriers (ATR 0.01 % of price): even p = 0.9 cannot pay ~0.2 % of round-trip costs.
        var ev = ExpectedValueCalculator.Compute(0.9, atr: 5, price: 50_000, Profile, Costs);

        Assert.True(ev.ExpectedReturn < 0);
    }

    [Fact]
    public void Positive_expected_value_gives_a_long_with_the_label_barriers()
    {
        var strategy = Strategy(rawProbability: 0.6);

        var signal = strategy.Evaluate(Context(close: 50_000m, atr: 400));

        Assert.Equal(SignalDirection.Long, signal.Direction);
        Assert.Equal(50_000m - 800m, signal.StopLossPrice);
        Assert.Equal(50_000m + 1_200m, signal.TakeProfitPrice);
        Assert.Contains("EV +", signal.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_positive_expected_value_is_no_trade_with_its_reason()
    {
        var signal = Strategy(rawProbability: 0.2).Evaluate(Context(close: 50_000m, atr: 400));

        Assert.Equal(SignalDirection.NoTrade, signal.Direction);
        Assert.Equal(NoTradeReason.ExpectedValueTooLow, signal.Reason);
    }

    [Fact]
    public void Minimum_expected_return_raises_the_bar()
    {
        var context = Context(close: 50_000m, atr: 400);
        var ev = ExpectedValueCalculator.Compute(0.6, 400, 50_000, Profile, Costs).ExpectedReturn;

        Assert.Equal(SignalDirection.Long, Strategy(0.6, minExpectedReturn: ev - 1e-9).Evaluate(context).Direction);
        Assert.Equal(SignalDirection.NoTrade, Strategy(0.6, minExpectedReturn: ev + 1e-9).Evaluate(context).Direction);
    }

    [Fact]
    public void Decisions_use_the_calibrated_probability_not_the_raw_one()
    {
        // Overconfident model: raw 0.9 would give a positive EV; Platt (fitted on overconfident predictions)
        // pulls it down to ~0.73, where the EV is negative. ATR 60 at 50 000 puts the break-even between the two.
        var platt = ProbabilityCalibrator.FromJson(ReferencePackage.Resource("platt.json")).Value;
        var context = Context(close: 50_000m, atr: 60);
        var calibrated = platt.Calibrate(0.9);

        var raw = new ModelExpectedValueStrategy(new FakeModel(0.9, false), Identity(), Profile, Costs, 2, 3).Evaluate(context);
        var withCalibration = new ModelExpectedValueStrategy(new FakeModel(0.9, false), platt, Profile, Costs, 2, 3).Evaluate(context);

        Assert.InRange(calibrated, 0.6, 0.85);
        Assert.True(ExpectedValueCalculator.Compute(0.9, 60, 50_000, Profile, Costs).ExpectedReturn > 0);
        Assert.True(ExpectedValueCalculator.Compute(calibrated, 60, 50_000, Profile, Costs).ExpectedReturn < 0);
        Assert.Equal(SignalDirection.Long, raw.Direction);
        Assert.Equal(SignalDirection.NoTrade, withCalibration.Direction);
        Assert.Equal(NoTradeReason.ExpectedValueTooLow, withCalibration.Reason);
    }

    [Fact]
    public void Open_positions_are_left_to_their_barriers()
    {
        var context = Context(50_000m, 400) with { Position = new PositionView(Open, 50_000m, 1m, 49_200m, 51_200m) };

        Assert.Equal(SignalDirection.Hold, Strategy(0.9).Evaluate(context).Direction);
    }

    [Fact]
    public void Missing_inputs_are_reported_as_their_no_trade_reason()
    {
        Assert.Equal(NoTradeReason.InvalidMarketData, Strategy(0.6).Evaluate(Context(50_000m, atr: null)).Reason);
        Assert.Equal(NoTradeReason.FeaturesUnavailable, Strategy(0.6, throws: true).Evaluate(Context(50_000m, 400)).Reason);
    }

    [Fact]
    public void Identity_records_the_model_and_the_decision_parameters()
    {
        var identity = Strategy(0.6, minExpectedReturn: 0.0005).Identity;

        Assert.Equal("model-ev:fake-model", identity.Name);
        Assert.Equal("fake-model", identity.Parameters["modelId"]);
        Assert.Equal("0.0005", identity.Parameters["minExpectedReturn"]);
        Assert.Equal("None", identity.Parameters["calibration"]);
    }

    private static ModelExpectedValueStrategy Strategy(double rawProbability, double minExpectedReturn = 0, bool throws = false) =>
        new(new FakeModel(rawProbability, throws), Identity(), Profile, Costs, stopAtr: 2, targetAtr: 3, minExpectedReturn);

    private static ProbabilityCalibrator Identity() =>
        ProbabilityCalibrator.FromJson("""{"format":"omega-calibration-v1","method":"none","epsilon":0.001,"logitEpsilon":0.000001}""").Value;

    private static StrategyContext Context(decimal close, double? atr)
    {
        var candle = Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, Open, Open.AddMinutes(5).AddMilliseconds(-1),
            close, close + 10, close - 10, close, 1m, 1m, 1).Value;
        var values = new Dictionary<string, double?> { ["atr_14"] = atr, ["rsi_14"] = 55 };
        return new StrategyContext(candle, new FeatureVector("BTCUSDT", CandleInterval.FiveMinutes, Open, candle.CloseTimeUtc, "v", "h", values), null);
    }

    private sealed class FakeModel(double probability, bool throws) : IProbabilityModel
    {
        public string ModelId => "fake-model";

        public IReadOnlyList<string> Features { get; } = ["rsi_14"];

        public double PredictRaw(IReadOnlyDictionary<string, double?> features) =>
            throws ? throw new ArgumentException("Feature missing.") : probability;
    }
}
