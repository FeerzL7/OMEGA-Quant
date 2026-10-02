using System.Text.Json;
using Omega.Core.Results;

namespace Omega.Strategy.Calibration;

public enum CalibrationMethod
{
    None = 1,
    Platt = 2,
    Isotonic = 3,
}

public static class CalibrationErrors
{
    public const string InvalidDocument = "CALIBRATION_INVALID_DOCUMENT";
}

/// <summary>
/// Maps a model's raw probability to a calibrated one (CLAUDE.md §9). Loaded from the <c>calibration.json</c>
/// (format <c>omega-calibration-v1</c>) produced by the research pipeline next to each registered model; the
/// formulas are identical to the Python ones (a parity test checks it):
/// <list type="bullet">
/// <item>none: the raw probability.</item>
/// <item>platt: <c>sigmoid(a · logit(clip(p, logitEpsilon, 1 − logitEpsilon)) + b)</c>.</item>
/// <item>isotonic: linear interpolation between (x, y) points, constant outside [x₀, xₙ].</item>
/// </list>
/// The result is always clipped to [epsilon, 1 − epsilon]: a calibrated model never claims certainty.
/// </summary>
public sealed class ProbabilityCalibrator
{
    public const string Format = "omega-calibration-v1";

    private readonly double[] _x;
    private readonly double[] _y;

    private ProbabilityCalibrator(CalibrationMethod method, double epsilon, double logitEpsilon, double plattA, double plattB, double[] x, double[] y)
    {
        Method = method;
        Epsilon = epsilon;
        LogitEpsilon = logitEpsilon;
        PlattA = plattA;
        PlattB = plattB;
        _x = x;
        _y = y;
    }

    public CalibrationMethod Method { get; }

    public double Epsilon { get; }

    public double LogitEpsilon { get; }

    public double PlattA { get; }

    public double PlattB { get; }

    /// <param name="rawProbability">Model output in [0, 1].</param>
    public double Calibrate(double rawProbability)
    {
        if (!double.IsFinite(rawProbability) || rawProbability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rawProbability), rawProbability, "A probability must be in [0, 1].");
        }

        var calibrated = Method switch
        {
            CalibrationMethod.Platt => 1 / (1 + Math.Exp(-((PlattA * Logit(rawProbability)) + PlattB))),
            CalibrationMethod.Isotonic => Interpolate(rawProbability),
            _ => rawProbability,
        };

        return Math.Clamp(calibrated, Epsilon, 1 - Epsilon);
    }

    public static Result<ProbabilityCalibrator> FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.GetProperty("format").GetString() != Format)
            {
                return Invalid($"Unsupported format; expected {Format}.");
            }

            var epsilon = root.GetProperty("epsilon").GetDouble();
            var logitEpsilon = root.GetProperty("logitEpsilon").GetDouble();
            if (epsilon is <= 0 or >= 0.5 || logitEpsilon is <= 0 or >= 0.5)
            {
                return Invalid("epsilon and logitEpsilon must be in (0, 0.5).");
            }

            switch (root.GetProperty("method").GetString())
            {
                case "none":
                    return Result.Success(new ProbabilityCalibrator(CalibrationMethod.None, epsilon, logitEpsilon, 1, 0, [], []));

                case "platt":
                    var platt = root.GetProperty("platt");
                    var a = platt.GetProperty("a").GetDouble();
                    var b = platt.GetProperty("b").GetDouble();
                    if (!double.IsFinite(a) || !double.IsFinite(b) || a <= 0)
                    {
                        return Invalid("Platt parameters must be finite and the slope positive (a calibrator must not invert the ranking).");
                    }

                    return Result.Success(new ProbabilityCalibrator(CalibrationMethod.Platt, epsilon, logitEpsilon, a, b, [], []));

                case "isotonic":
                    var isotonic = root.GetProperty("isotonic");
                    var x = isotonic.GetProperty("x").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    var y = isotonic.GetProperty("y").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    if (x.Length == 0 || x.Length != y.Length)
                    {
                        return Invalid("Isotonic x and y must be non-empty and of equal length.");
                    }

                    for (var i = 0; i < x.Length; i++)
                    {
                        if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]) || x[i] is < 0 or > 1 || y[i] is < 0 or > 1
                            || (i > 0 && (x[i] <= x[i - 1] || y[i] < y[i - 1])))
                        {
                            return Invalid("Isotonic points must be in [0, 1], x strictly increasing and y non-decreasing.");
                        }
                    }

                    return Result.Success(new ProbabilityCalibrator(CalibrationMethod.Isotonic, epsilon, logitEpsilon, 1, 0, x, y));

                default:
                    return Invalid("Unknown calibration method.");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Invalid($"Malformed calibration document: {ex.Message}");
        }
    }

    private double Logit(double p)
    {
        var clipped = Math.Clamp(p, LogitEpsilon, 1 - LogitEpsilon);
        return Math.Log(clipped / (1 - clipped));
    }

    /// <summary>Same as numpy.interp: constant outside the points, linear between them.</summary>
    private double Interpolate(double p)
    {
        if (p <= _x[0])
        {
            return _y[0];
        }

        if (p >= _x[^1])
        {
            return _y[^1];
        }

        var index = Array.BinarySearch(_x, p);
        if (index >= 0)
        {
            return _y[index];
        }

        var upper = ~index;
        var lower = upper - 1;
        var slope = (_y[upper] - _y[lower]) / (_x[upper] - _x[lower]);
        return _y[lower] + (slope * (p - _x[lower]));
    }

    private static Result<ProbabilityCalibrator> Invalid(string message) =>
        Result.Failure<ProbabilityCalibrator>(new Error(CalibrationErrors.InvalidDocument, message));
}
