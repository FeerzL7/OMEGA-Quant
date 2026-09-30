using System.Globalization;
using Omega.Core.MarketData;

namespace Omega.Features.Indicators;

// Recursive averages (EMA, Wilder) are initialized inside the window, so a value depends only on its
// Lookback candles and is identical in backtests and live. Windows are long enough for the
// initialization to be negligible: 5 x period for EMA (residual weight ~ e^-10) and 11 x period + 1 for
// Wilder averages (residual weight ~ e^-10). Values differ slightly from charting tools that use the
// full history; that is deliberate and documented in docs/FEATURES.md.

internal static class Lookbacks
{
    public const int EmaFactor = 5;
    public const int WilderFactor = 10;

    public static int Ema(int period) => EmaFactor * period;

    /// <summary>One extra candle for the first price change, period values for the seed, then WilderFactor x period of smoothing.</summary>
    public static int Wilder(int period) => 1 + period + (WilderFactor * period);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Text(FormattableString text) => text.ToString(Invariant);
}

/// <summary>log(C_t / C_{t-k}).</summary>
internal sealed class LogReturn(int bars) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"log_return_{bars}"),
        Purpose: Lookbacks.Text($"Price change over the last {bars} candle(s); scale-free momentum."),
        Formula: Lookbacks.Text($"ln(close[t] / close[t-{bars}])"),
        Lookback: bars + 1,
        RequiredData: "close",
        LeakageRisk: "Low. Uses only closed candles up to t. Must never be aligned with the label of candle t (that would be the future return).");

    public double? Compute(ReadOnlySpan<Candle> window) =>
        Math.Log((double)window[^1].Close / (double)window[0].Close);
}

/// <summary>Simple moving average of closes.</summary>
internal sealed class SimpleMovingAverage(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"sma_{period}"),
        Purpose: "Price level smoothed over the period; used by trend rules (baseline strategy).",
        Formula: Lookbacks.Text($"mean(close[t-{period - 1}..t])"),
        Lookback: period,
        RequiredData: "close",
        LeakageRisk: "Low. Closed candles up to t only. Price-level (non-stationary): normalize before using it in ML models.");

    public double? Compute(ReadOnlySpan<Candle> window) => Series.Mean(Series.Closes(window));
}

/// <summary>Exponential moving average of closes.</summary>
internal sealed class ExponentialMovingAverage(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"ema_{period}"),
        Purpose: "Price level smoothed with more weight on recent candles; used by trend rules.",
        Formula: Lookbacks.Text($"EMA with alpha = 2/({period}+1), seeded with the mean of the first {period} closes of the window, then ema = alpha*close + (1-alpha)*ema"),
        Lookback: Lookbacks.Ema(period),
        RequiredData: "close",
        LeakageRisk: "Low. Closed candles up to t only; initialized inside the window. Price-level (non-stationary): normalize before ML.");

    public double? Compute(ReadOnlySpan<Candle> window) =>
        Series.SeededExponentialAverage(Series.Closes(window), period, Series.EmaAlpha(period))[^1];
}

/// <summary>Relative distance of the close to its EMA.</summary>
internal sealed class DistanceFromEma(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"dist_ema_{period}"),
        Purpose: "How far price is stretched above (+) or below (-) its trend, as a fraction; scale-free.",
        Formula: Lookbacks.Text($"close[t] / ema_{period}[t] - 1"),
        Lookback: Lookbacks.Ema(period),
        RequiredData: "close",
        LeakageRisk: Lookbacks.Text($"Low. Same inputs as ema_{period}."));

    public double? Compute(ReadOnlySpan<Candle> window)
    {
        var closes = Series.Closes(window);
        var ema = Series.SeededExponentialAverage(closes, period, Series.EmaAlpha(period))[^1];
        return (closes[^1] / ema) - 1;
    }
}

/// <summary>Wilder's Relative Strength Index.</summary>
internal sealed class RelativeStrengthIndex(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"rsi_{period}"),
        Purpose: "Balance of recent gains versus losses (0-100); overbought/oversold momentum.",
        Formula: Lookbacks.Text($"change = close[i]-close[i-1]; avgGain/avgLoss = Wilder averages (alpha 1/{period}, seeded with the mean of the first {period} values) of max(change,0) and max(-change,0); RSI = 100 - 100/(1 + avgGain/avgLoss); 100 if avgLoss = 0; undefined (null) if both are 0"),
        Lookback: Lookbacks.Wilder(period),
        RequiredData: "close",
        LeakageRisk: "Low. Closed candles up to t only; initialized inside the window.");

    public double? Compute(ReadOnlySpan<Candle> window)
    {
        var closes = Series.Closes(window);
        var gains = new double[closes.Length - 1];
        var losses = new double[closes.Length - 1];

        for (var i = 1; i < closes.Length; i++)
        {
            var change = closes[i] - closes[i - 1];
            gains[i - 1] = Math.Max(change, 0);
            losses[i - 1] = Math.Max(-change, 0);
        }

        var alpha = Series.WilderAlpha(period);
        var averageGain = Series.SeededExponentialAverage(gains, period, alpha)[^1];
        var averageLoss = Series.SeededExponentialAverage(losses, period, alpha)[^1];

        if (averageLoss == 0)
        {
            return averageGain == 0 ? null : 100;
        }

        return 100 - (100 / (1 + (averageGain / averageLoss)));
    }
}

/// <summary>Wilder's Average True Range.</summary>
internal sealed class AverageTrueRange(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"atr_{period}"),
        Purpose: "Typical candle range including gaps; volatility in price units (used for stops and sizing).",
        Formula: Lookbacks.Text($"TR[i] = max(high-low, |high-close[i-1]|, |low-close[i-1]|); ATR = Wilder average (alpha 1/{period}, seeded with the mean of the first {period} TR)"),
        Lookback: Lookbacks.Wilder(period),
        RequiredData: "high, low, close",
        LeakageRisk: "Low. Closed candles up to t only. Price units (non-stationary): divide by close before ML.");

    public double? Compute(ReadOnlySpan<Candle> window)
    {
        var trueRanges = new double[window.Length - 1];
        for (var i = 1; i < window.Length; i++)
        {
            trueRanges[i - 1] = Series.TrueRange(window, i);
        }

        return Series.SeededExponentialAverage(trueRanges, period, Series.WilderAlpha(period))[^1];
    }
}

/// <summary>MACD line, signal or histogram.</summary>
internal sealed class MovingAverageConvergenceDivergence(int fast, int slow, int signal, MovingAverageConvergenceDivergence.Output output) : IFeature
{
    public enum Output
    {
        Line = 1,
        Signal = 2,
        Histogram = 3,
    }

    public FeatureDefinition Definition { get; } = new(
        Name: output switch
        {
            Output.Line => "macd_line",
            Output.Signal => "macd_signal",
            _ => "macd_histogram",
        },
        Purpose: output switch
        {
            Output.Line => Lookbacks.Text($"Momentum as the gap between fast and slow trends (EMA {fast} vs {slow}), price units."),
            Output.Signal => Lookbacks.Text($"Smoothed MACD line (EMA {signal}); crossings mark momentum shifts."),
            _ => "MACD line minus signal: acceleration of momentum.",
        },
        Formula: Lookbacks.Text($"line = EMA{fast}(close) - EMA{slow}(close) (each seeded in the window); signal = EMA{signal}(line) seeded with the mean of the first {signal} line values; histogram = line - signal"),
        Lookback: Lookbacks.Ema(slow) + Lookbacks.Ema(signal),
        RequiredData: "close",
        LeakageRisk: "Low. Closed candles up to t only; all averages initialized inside the window. Price units: normalize before ML.");

    public double? Compute(ReadOnlySpan<Candle> window)
    {
        var closes = Series.Closes(window);
        var fastEma = Series.SeededExponentialAverage(closes, fast, Series.EmaAlpha(fast));
        var slowEma = Series.SeededExponentialAverage(closes, slow, Series.EmaAlpha(slow));

        // The line exists once the slow EMA exists (index slow - 1).
        var line = new double[closes.Length - (slow - 1)];
        for (var i = 0; i < line.Length; i++)
        {
            line[i] = fastEma[i + slow - 1] - slowEma[i + slow - 1];
        }

        var lineValue = line[^1];
        var signalValue = Series.SeededExponentialAverage(line, signal, Series.EmaAlpha(signal))[^1];

        return output switch
        {
            Output.Line => lineValue,
            Output.Signal => signalValue,
            _ => lineValue - signalValue,
        };
    }
}

/// <summary>Standard deviation of one-candle log returns.</summary>
internal sealed class RealizedVolatility(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"volatility_{period}"),
        Purpose: "Recent return dispersion per candle (not annualized); scale-free volatility regime.",
        Formula: Lookbacks.Text($"sample standard deviation (n-1) of ln(close[i]/close[i-1]) over the last {period} returns"),
        Lookback: period + 1,
        RequiredData: "close",
        LeakageRisk: "Low. Closed candles up to t only.");

    public double? Compute(ReadOnlySpan<Candle> window)
    {
        var returns = new double[window.Length - 1];
        for (var i = 1; i < window.Length; i++)
        {
            returns[i - 1] = Math.Log((double)window[i].Close / (double)window[i - 1].Close);
        }

        return Series.SampleStandardDeviation(returns);
    }
}

/// <summary>Traded base-asset volume of candle t.</summary>
internal sealed class Volume : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: "volume",
        Purpose: "Participation during the candle, in base-asset units (BTC for BTCUSDT).",
        Formula: "base_volume[t]",
        Lookback: 1,
        RequiredData: "base_volume",
        LeakageRisk: "Low, provided only closed candles are used (an in-progress candle's volume is incomplete). Non-stationary: normalize before ML.");

    public double? Compute(ReadOnlySpan<Candle> window) => (double)window[^1].BaseVolume;
}

/// <summary>How unusual the volume of candle t is versus the previous candles.</summary>
internal sealed class VolumeZScore(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"volume_zscore_{period}"),
        Purpose: "Volume of candle t relative to recent activity; flags unusual participation. Scale-free.",
        Formula: Lookbacks.Text($"(volume[t] - mean(volume[t-{period}..t-1])) / sampleStd(volume[t-{period}..t-1]); undefined (null) if the std is 0"),
        Lookback: period + 1,
        RequiredData: "base_volume",
        LeakageRisk: "Low. The baseline excludes candle t itself, so the value is not diluted by the candle it describes.");

    public double? Compute(ReadOnlySpan<Candle> window)
    {
        var baseline = new double[window.Length - 1];
        for (var i = 0; i < baseline.Length; i++)
        {
            baseline[i] = (double)window[i].BaseVolume;
        }

        var deviation = Series.SampleStandardDeviation(baseline);
        return deviation == 0 ? null : ((double)window[^1].BaseVolume - Series.Mean(baseline)) / deviation;
    }
}

/// <summary>Wilder's Average Directional Index: trend strength regardless of direction.</summary>
internal sealed class AverageDirectionalIndex(int period) : IFeature
{
    public FeatureDefinition Definition { get; } = new(
        Name: Lookbacks.Text($"adx_{period}"),
        Purpose: "Trend strength (0-100) regardless of direction; distinguishes trending from ranging markets.",
        Formula: Lookbacks.Text($"+DM = up > down and up > 0 ? up : 0, -DM = down > up and down > 0 ? down : 0 (up = high-high[i-1], down = low[i-1]-low); Wilder averages (alpha 1/{period}, seeded) of +DM, -DM and TR; +DI = 100*avg(+DM)/avg(TR), -DI likewise; DX = 100*|+DI - -DI|/(+DI + -DI) (0 if the sum is 0); ADX = Wilder average of DX seeded with the mean of the first {period} DX"),
        Lookback: Lookbacks.Wilder(period) + period,
        RequiredData: "high, low, close",
        LeakageRisk: "Low. Closed candles up to t only; initialized inside the window.");

    public double? Compute(ReadOnlySpan<Candle> window)
    {
        var count = window.Length - 1;
        var plusDm = new double[count];
        var minusDm = new double[count];
        var trueRanges = new double[count];

        for (var i = 1; i < window.Length; i++)
        {
            var up = (double)(window[i].High - window[i - 1].High);
            var down = (double)(window[i - 1].Low - window[i].Low);
            plusDm[i - 1] = up > down && up > 0 ? up : 0;
            minusDm[i - 1] = down > up && down > 0 ? down : 0;
            trueRanges[i - 1] = Series.TrueRange(window, i);
        }

        var alpha = Series.WilderAlpha(period);
        var smoothedPlus = Series.SeededExponentialAverage(plusDm, period, alpha);
        var smoothedMinus = Series.SeededExponentialAverage(minusDm, period, alpha);
        var smoothedRange = Series.SeededExponentialAverage(trueRanges, period, alpha);

        var dx = new double[count - (period - 1)];
        for (var i = 0; i < dx.Length; i++)
        {
            var range = smoothedRange[i + period - 1];
            var plusDi = range == 0 ? 0 : 100 * smoothedPlus[i + period - 1] / range;
            var minusDi = range == 0 ? 0 : 100 * smoothedMinus[i + period - 1] / range;
            var sum = plusDi + minusDi;
            dx[i] = sum == 0 ? 0 : 100 * Math.Abs(plusDi - minusDi) / sum;
        }

        return Series.SeededExponentialAverage(dx, period, alpha)[^1];
    }
}
