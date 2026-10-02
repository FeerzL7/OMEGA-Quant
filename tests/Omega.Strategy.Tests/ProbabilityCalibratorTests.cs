using System.Globalization;
using System.Reflection;
using Omega.Strategy.Calibration;

namespace Omega.Strategy.Tests;

public class ProbabilityCalibratorTests
{
    [Fact]
    public void Every_method_reproduces_the_python_calibration_exactly()
    {
        var calibrators = new Dictionary<string, ProbabilityCalibrator>
        {
            ["none"] = Load("none.json"),
            ["platt"] = Load("platt.json"),
            ["isotonic"] = Load("isotonic.json"),
        };
        var lines = Resource("calibration_cases.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines[0].Split(',');
        var mismatches = new List<string>();

        foreach (var line in lines.Skip(1))
        {
            var values = line.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            for (var column = 1; column < header.Length; column++)
            {
                var actual = calibrators[header[column]].Calibrate(values[0]);
                if (Math.Abs(actual - values[column]) > 1e-12)
                {
                    mismatches.Add($"{header[column]}({values[0]}): expected {values[column]}, got {actual}");
                }
            }
        }

        Assert.True(lines.Length > 1000);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Output_never_claims_certainty_and_preserves_the_ranking()
    {
        foreach (var calibrator in new[] { Load("none.json"), Load("platt.json"), Load("isotonic.json") })
        {
            var outputs = Enumerable.Range(0, 1001).Select(i => calibrator.Calibrate(i / 1000.0)).ToArray();

            Assert.All(outputs, p => Assert.InRange(p, calibrator.Epsilon, 1 - calibrator.Epsilon));
            Assert.True(outputs.Zip(outputs.Skip(1)).All(pair => pair.Second >= pair.First), calibrator.Method.ToString());
        }
    }

    [Fact]
    public void Platt_corrects_the_overconfidence_it_was_fitted_on()
    {
        var platt = Load("platt.json");

        Assert.Equal(CalibrationMethod.Platt, platt.Method);
        Assert.InRange(platt.PlattA, 0.35, 0.55);          // the reference model was overconfident by a factor of 2.2
        Assert.True(platt.Calibrate(0.95) < 0.85);
        Assert.True(platt.Calibrate(0.05) > 0.15);
    }

    [Theory]
    [InlineData("""{"format":"other","method":"none","epsilon":0.001,"logitEpsilon":0.000001}""")]
    [InlineData("""{"format":"omega-calibration-v1","method":"magic","epsilon":0.001,"logitEpsilon":0.000001}""")]
    [InlineData("""{"format":"omega-calibration-v1","method":"none","epsilon":0.7,"logitEpsilon":0.000001}""")]
    [InlineData("""{"format":"omega-calibration-v1","method":"platt","epsilon":0.001,"logitEpsilon":0.000001,"platt":{"a":-1.0,"b":0.0}}""")]
    [InlineData("""{"format":"omega-calibration-v1","method":"isotonic","epsilon":0.001,"logitEpsilon":0.000001,"isotonic":{"x":[0.1,0.5],"y":[0.2]}}""")]
    [InlineData("""{"format":"omega-calibration-v1","method":"isotonic","epsilon":0.001,"logitEpsilon":0.000001,"isotonic":{"x":[0.5,0.1],"y":[0.2,0.3]}}""")]
    [InlineData("""{"format":"omega-calibration-v1","method":"isotonic","epsilon":0.001,"logitEpsilon":0.000001,"isotonic":{"x":[0.1,0.5],"y":[0.6,0.3]}}""")]
    [InlineData("""{"format":"omega-calibration-v1","method":"platt","epsilon":0.001,"logitEpsilon":0.000001}""")]
    [InlineData("not json")]
    public void Invalid_documents_are_rejected(string json)
    {
        Assert.Equal(CalibrationErrors.InvalidDocument, ProbabilityCalibrator.FromJson(json).Error!.Code);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Raw_probabilities_outside_zero_one_are_rejected(double raw)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Load("none.json").Calibrate(raw));
    }

    private static ProbabilityCalibrator Load(string resource) => ProbabilityCalibrator.FromJson(Resource(resource)).Value;

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
