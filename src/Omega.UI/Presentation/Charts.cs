using System.Globalization;

namespace Omega.UI.Presentation;

/// <summary>One candle drawn: x centre, body and wick in pixels.</summary>
public sealed record CandleShape(double X, double Width, double BodyTop, double BodyBottom, double WickTop, double WickBottom, bool Rising, DateTimeOffset OpenTimeUtc);

public sealed record AxisTick(double Position, string Label);

/// <summary>A horizontal reference line (entry, stop, target).</summary>
public sealed record LevelLine(double Y, string Label, string Kind);

public sealed record CandleChartModel(
    double Width, double Height, IReadOnlyList<CandleShape> Candles, IReadOnlyList<AxisTick> PriceTicks, IReadOnlyList<AxisTick> TimeTicks,
    IReadOnlyList<LevelLine> Levels, decimal Min, decimal Max);

public sealed record LineChartModel(double Width, double Height, string Points, IReadOnlyList<AxisTick> ValueTicks, IReadOnlyList<AxisTick> TimeTicks, double? ZeroY);

/// <summary>
/// Chart geometry in plain C# (rendered as SVG by Razor components): no JavaScript library, no CDN, and the arithmetic
/// is unit-tested. Price axes are never truncated to exaggerate moves silently: the range is the data range plus padding,
/// and the labels say so.
/// </summary>
public static class Charts
{
    public const double LeftMargin = 8, RightMargin = 64, TopMargin = 8, BottomMargin = 22;

    public static CandleChartModel Candles(
        IReadOnlyList<(DateTimeOffset Open, decimal O, decimal H, decimal L, decimal C)> candles, double width, double height,
        IReadOnlyList<(decimal Price, string Label, string Kind)>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(candles);
        levels ??= [];
        if (candles.Count == 0)
        {
            return new CandleChartModel(width, height, [], [], [], [], 0m, 0m);
        }

        var values = candles.SelectMany(c => new[] { c.H, c.L }).Concat(levels.Select(l => l.Price)).ToList();
        var (min, max) = Padded(values.Min(), values.Max());
        var plotWidth = width - LeftMargin - RightMargin;
        var step = plotWidth / candles.Count;
        double Y(decimal price) => TopMargin + ((double)((max - price) / (max - min)) * (height - TopMargin - BottomMargin));

        var shapes = candles.Select((c, i) => new CandleShape(
            LeftMargin + (step * (i + 0.5)), Math.Max(1, step * 0.7),
            Y(Math.Max(c.O, c.C)), Y(Math.Min(c.O, c.C)), Y(c.H), Y(c.L), c.C >= c.O, c.Open)).ToList();

        var priceTicks = Ticks(min, max, 5).Select(p => new AxisTick(Y(p), p.ToString("#,##0.##", CultureInfo.InvariantCulture))).ToList();
        var timeTicks = Enumerable.Range(0, 5)
            .Select(k => (int)Math.Round(k * (candles.Count - 1) / 4.0))
            .Distinct()
            .Select(i => new AxisTick(shapes[i].X, candles[i].Open.UtcDateTime.ToString("dd HH:mm", CultureInfo.InvariantCulture)))
            .ToList();

        return new CandleChartModel(width, height, shapes, priceTicks, timeTicks,
            [.. levels.Select(l => new LevelLine(Y(l.Price), l.Label, l.Kind))], min, max);
    }

    public static LineChartModel Line(IReadOnlyList<(DateTimeOffset Time, double Value)> points, double width, double height, bool includeZero = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return new LineChartModel(width, height, string.Empty, [], [], null);
        }

        var low = points.Min(p => p.Value);
        var high = points.Max(p => p.Value);
        if (includeZero)
        {
            low = Math.Min(low, 0);
            high = Math.Max(high, 0);
        }

        var (min, max) = Padded((decimal)low, (decimal)high);
        var start = points[0].Time;
        var span = Math.Max(1, (points[^1].Time - start).TotalSeconds);
        var plotWidth = width - LeftMargin - RightMargin;
        double X(DateTimeOffset t) => LeftMargin + ((t - start).TotalSeconds / span * plotWidth);
        double Y(double v) => TopMargin + ((double)(max - (decimal)v) / (double)(max - min) * (height - TopMargin - BottomMargin));

        var polyline = string.Join(' ', points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{X(p.Time):0.##},{Y(p.Value):0.##}")));
        var valueTicks = Ticks(min, max, 4).Select(v => new AxisTick(Y((double)v), v.ToString("#,##0.####", CultureInfo.InvariantCulture))).ToList();
        var timeTicks = Enumerable.Range(0, 4)
            .Select(k => points[(int)Math.Round(k * (points.Count - 1) / 3.0)].Time)
            .Distinct()
            .Select(t => new AxisTick(X(t), t.UtcDateTime.ToString("MM-dd", CultureInfo.InvariantCulture)))
            .ToList();

        return new LineChartModel(width, height, polyline, valueTicks, timeTicks, includeZero ? Y(0) : null);
    }

    /// <summary>Data range plus 5 % padding; a flat series gets a symmetric range so it is drawn mid-height.</summary>
    internal static (decimal Min, decimal Max) Padded(decimal low, decimal high)
    {
        if (high == low)
        {
            var half = low == 0 ? 1m : Math.Abs(low) * 0.01m;
            return (low - half, high + half);
        }

        var pad = (high - low) * 0.05m;
        return (low - pad, high + pad);
    }

    /// <summary>"Nice" round tick values inside [min, max].</summary>
    internal static IReadOnlyList<decimal> Ticks(decimal min, decimal max, int count)
    {
        var raw = (double)(max - min) / count;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = (decimal)(new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw));
        var first = Math.Ceiling(min / step) * step;
        var ticks = new List<decimal>();
        for (var v = first; v <= max; v += step)
        {
            ticks.Add(v);
        }

        return ticks;
    }
}
