using System.Globalization;
using System.Reflection;
using Omega.Core.Trading;
using Omega.Strategy.Models;

namespace Omega.Strategy.Tests;

public sealed class ModelPackageTests : IDisposable
{
    private readonly string _directory = ReferencePackage.Extract();

    [Theory]
    [InlineData("model_package", "model_cases.csv")]       // logistic regression: float64 probabilities
    [InlineData("model_package_rf", "model_cases_rf.csv")]  // random forest: float32 probabilities
    public void Raw_calibrated_and_expected_values_match_the_python_pipeline(string packageName, string casesName)
    {
        var directory = ReferencePackage.Extract(packageName);
        var package = ModelPackage.Load(directory).Value;
        var costs = new TradingCosts(0.001m, 1m, 2m);
        var lines = ReferencePackage.Resource(casesName).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines[0].Split(',');
        var worst = (raw: 0.0, calibrated: 0.0, ev: 0.0);

        foreach (var line in lines.Skip(1))
        {
            var v = line.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var features = header.Take(8).Select((name, i) => (name, value: (double?)v[i])).ToDictionary(p => p.name, p => p.value);

            var raw = package.PredictRaw(features);
            var calibrated = package.Calibrator.Calibrate(raw);
            var ev = ExpectedValueCalculator.Compute(calibrated, v[8], v[9], package.Profile, costs).ExpectedReturn;

            worst = (Math.Max(worst.raw, Math.Abs(raw - v[10])), Math.Max(worst.calibrated, Math.Abs(calibrated - v[11])), Math.Max(worst.ev, Math.Abs(ev - v[12])));
        }

        package.Dispose();
        Directory.Delete(directory, recursive: true);
        Assert.True(lines.Length > 100);
        Assert.True(worst.raw <= 1e-12, $"raw {worst.raw}");
        Assert.True(worst.calibrated <= 1e-12, $"calibrated {worst.calibrated}");
        Assert.True(worst.ev <= 1e-12, $"ev {worst.ev}");
    }

    [Fact]
    public void Package_exposes_its_label_barriers_and_training_period()
    {
        using var package = ModelPackage.Load(_directory).Value;

        Assert.Equal((2.0, 3.0, 48), (package.StopAtr, package.TargetAtr, package.HorizonCandles));
        Assert.StartsWith("logistic_regression-", package.ModelId, StringComparison.Ordinal);
        Assert.Equal("2025-01-01T00:00:00+00:00..2025-06-01T00:00:00+00:00", package.TrainingPeriod);
        Assert.Equal(8, package.Features.Count);
    }

    [Fact]
    public void A_corrupted_model_file_is_refused()
    {
        var path = Path.Combine(_directory, "model.onnx");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Equal(ModelPackageErrors.Invalid, ModelPackage.Load(_directory).Error!.Code);
    }

    [Fact]
    public void A_valid_model_that_is_not_the_registered_one_is_refused()
    {
        // The ONNX file is intact and loadable, but it is not the file the manifest describes
        // (for example, a model replaced after registration).
        var manifestPath = Path.Combine(_directory, "manifest.json");
        var manifest = File.ReadAllText(manifestPath);
        var registeredHash = System.Text.RegularExpressions.Regex.Match(manifest, "\"onnxSha256\": \"([0-9a-f]{64})\"").Groups[1].Value;
        File.WriteAllText(manifestPath, manifest.Replace(registeredHash, new string('0', 64), StringComparison.Ordinal));

        var result = ModelPackage.Load(_directory);

        Assert.Equal(64, registeredHash.Length);
        Assert.Equal(ModelPackageErrors.Invalid, result.Error!.Code);
        Assert.Contains("hash", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_without_calibration_or_profile_is_incomplete()
    {
        File.Delete(Path.Combine(_directory, "outcome_profile.json"));

        Assert.Equal(ModelPackageErrors.Incomplete, ModelPackage.Load(_directory).Error!.Code);
        Assert.Equal(ModelPackageErrors.NotFound, ModelPackage.Load(Path.Combine(_directory, "nope")).Error!.Code);
    }

    [Fact]
    public void Missing_input_features_are_reported_not_guessed()
    {
        using var package = ModelPackage.Load(_directory).Value;

        Assert.Throws<ArgumentException>(() => package.PredictRaw(new Dictionary<string, double?> { ["rsi_14"] = 50 }));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

internal static class ReferencePackage
{
    public static string Extract(string package = "model_package")
    {
        var directory = Directory.CreateTempSubdirectory("omega-model-").FullName;
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = package + "/";
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(Path.Combine(directory, name[prefix.Length..]));
            stream.CopyTo(file);
        }

        return directory;
    }

    public static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
