using System.Globalization;
using Omega.Core.Trading;
using Omega.Strategy.Calibration;

namespace Omega.Strategy.Models;

/// <summary>
/// Model-driven strategy (Phase 9): raw probability (ONNX) → calibrated probability → expected value after costs.
/// Goes long only when the expected return exceeds <paramref name="minExpectedReturn"/> (0 by default: any positive
/// edge after costs). It never uses a probability threshold such as "confidence > 85 %" (CLAUDE.md §7, §9).
/// </summary>
/// <remarks>
/// The position uses the barriers of the label the model was trained on (close ∓ ATR multiples) and is left to them
/// and to the timeout, exactly as in the training labels; the strategy does not exit early.
/// </remarks>
public sealed class ModelExpectedValueStrategy(
    IProbabilityModel model,
    ProbabilityCalibrator calibrator,
    OutcomeProfile profile,
    TradingCosts costs,
    double stopAtr,
    double targetAtr,
    double minExpectedReturn = 0) : IStrategy
{
    public const string NamePrefix = "model-ev";

    public StrategyIdentity Identity { get; } = new($"{NamePrefix}:{model.ModelId}", "1", new Dictionary<string, string>
    {
        ["modelId"] = model.ModelId,
        ["calibration"] = calibrator.Method.ToString(),
        ["minExpectedReturn"] = minExpectedReturn.ToString("R", CultureInfo.InvariantCulture),
        ["stopAtr"] = stopAtr.ToString("R", CultureInfo.InvariantCulture),
        ["targetAtr"] = targetAtr.ToString("R", CultureInfo.InvariantCulture),
        ["profileWinMean"] = profile.WinMean.ToString("R", CultureInfo.InvariantCulture),
        ["profileLossMean"] = profile.LossMean.ToString("R", CultureInfo.InvariantCulture),
        ["feeRate"] = costs.FeeRate.ToString(CultureInfo.InvariantCulture),
        ["spreadBps"] = costs.SpreadBps.ToString(CultureInfo.InvariantCulture),
        ["slippageBps"] = costs.SlippageBps.ToString(CultureInfo.InvariantCulture),
    });

    public Signal Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Position is not null)
        {
            return Signal.Hold("Position managed by its barriers and timeout.");
        }

        if (context.Features.Values.GetValueOrDefault("atr_14") is not { } atr || atr <= 0)
        {
            return Signal.NoTrade(NoTradeReason.InvalidMarketData, "ATR14 is unavailable or zero.");
        }

        double raw;
        try
        {
            raw = model.PredictRaw(context.Features.Values);
        }
        catch (ArgumentException)
        {
            return Signal.NoTrade(NoTradeReason.FeaturesUnavailable, "A model input feature is missing.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            return Signal.NoTrade(NoTradeReason.ModelUnavailable, $"Model inference failed: {ex.Message}");
        }

        var calibrated = calibrator.Calibrate(raw);
        var close = context.Candle.Close;
        var ev = ExpectedValueCalculator.Compute(calibrated, atr, (double)close, profile, costs);
        var explanation = string.Create(CultureInfo.InvariantCulture,
            $"P raw {raw:0.0000}, calibrated {calibrated:0.0000}; EV {ev.ExpectedReturn:+0.00000;-0.00000} (gain {ev.Gain:0.00000}, loss {ev.Loss:0.00000}, costs {ev.Costs:0.00000}).");

        var metrics = new Dictionary<string, double>
        {
            ["raw_probability"] = raw,
            ["calibrated_probability"] = calibrated,
            ["expected_return"] = ev.ExpectedReturn,
            ["gain"] = ev.Gain,
            ["loss"] = ev.Loss,
            ["costs"] = ev.Costs,
        };

        if (ev.ExpectedReturn <= minExpectedReturn)
        {
            return Signal.NoTrade(NoTradeReason.ExpectedValueTooLow, explanation) with { Metrics = metrics };
        }

        var stop = close - ((decimal)stopAtr * (decimal)atr);
        if (stop <= 0)
        {
            return Signal.NoTrade(NoTradeReason.InvalidMarketData, "The stop loss would not be positive.");
        }

        return Signal.Long(stop, close + ((decimal)targetAtr * (decimal)atr), explanation) with { Metrics = metrics };
    }
}
