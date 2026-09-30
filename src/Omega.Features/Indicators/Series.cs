using Omega.Core.MarketData;

namespace Omega.Features.Indicators;

/// <summary>Numerical building blocks. Prices enter as decimals and are converted once to double.</summary>
internal static class Series
{
    public static double[] Closes(ReadOnlySpan<Candle> window)
    {
        var values = new double[window.Length];
        for (var i = 0; i < window.Length; i++)
        {
            values[i] = (double)window[i].Close;
        }

        return values;
    }

    public static double Mean(ReadOnlySpan<double> values)
    {
        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Length;
    }

    /// <summary>Sample standard deviation (n - 1).</summary>
    public static double SampleStandardDeviation(ReadOnlySpan<double> values)
    {
        var mean = Mean(values);
        var squares = 0.0;
        foreach (var value in values)
        {
            squares += (value - mean) * (value - mean);
        }

        return Math.Sqrt(squares / (values.Length - 1));
    }

    /// <summary>
    /// Exponential average with smoothing <paramref name="alpha"/>, seeded with the simple mean of the first
    /// <paramref name="period"/> values. Element i is NaN for i &lt; period - 1.
    /// </summary>
    public static double[] SeededExponentialAverage(ReadOnlySpan<double> values, int period, double alpha)
    {
        var result = new double[values.Length];
        Array.Fill(result, double.NaN);

        var average = Mean(values[..period]);
        result[period - 1] = average;

        for (var i = period; i < values.Length; i++)
        {
            average = (alpha * values[i]) + ((1 - alpha) * average);
            result[i] = average;
        }

        return result;
    }

    /// <summary>EMA smoothing: 2 / (period + 1).</summary>
    public static double EmaAlpha(int period) => 2.0 / (period + 1);

    /// <summary>Wilder smoothing: 1 / period.</summary>
    public static double WilderAlpha(int period) => 1.0 / period;

    /// <summary>True range for candle i (i &gt;= 1): max(H - L, |H - C(i-1)|, |L - C(i-1)|).</summary>
    public static double TrueRange(ReadOnlySpan<Candle> window, int i)
    {
        var high = (double)window[i].High;
        var low = (double)window[i].Low;
        var previousClose = (double)window[i - 1].Close;

        return Math.Max(high - low, Math.Max(Math.Abs(high - previousClose), Math.Abs(low - previousClose)));
    }
}
