using System.Text.Json;

namespace Omega.Application.Research;

/// <summary>A registered model package as found on disk.</summary>
/// <param name="ModelId">Directory name and model id.</param>
/// <param name="ModelType">logistic_regression, random_forest...</param>
/// <param name="TrainingPeriod">Period the model was trained on.</param>
/// <param name="DatasetId">Dataset it was trained on.</param>
/// <param name="CalibrationMethod">Calibration selected in Phase 8, or null if not calibrated yet.</param>
/// <param name="ReadyForExpectedValue">True when model, calibration and outcome profile are all present.</param>
public sealed record ModelSummary(string ModelId, string ModelType, string TrainingPeriod, string DatasetId, string? CalibrationMethod, bool ReadyForExpectedValue);

/// <summary>Lists the model packages in the research models directory.</summary>
public static class ModelRegistry
{
    public static IReadOnlyList<ModelSummary> List(string modelsDirectory)
    {
        if (!Directory.Exists(modelsDirectory))
        {
            return [];
        }

        var models = new List<ModelSummary>();
        foreach (var directory in Directory.GetDirectories(modelsDirectory).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = manifest.RootElement;
                string? calibration = null;
                var calibrationPath = Path.Combine(directory, "calibration.json");
                if (File.Exists(calibrationPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(calibrationPath));
                    calibration = document.RootElement.GetProperty("method").GetString();
                }

                models.Add(new ModelSummary(
                    root.GetProperty("modelId").GetString()!,
                    root.GetProperty("modelType").GetString()!,
                    root.GetProperty("trainingPeriod").GetString()!,
                    root.GetProperty("datasetId").GetString()!,
                    calibration,
                    calibration is not null && File.Exists(Path.Combine(directory, "outcome_profile.json")) && File.Exists(Path.Combine(directory, "model.onnx"))));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // A malformed package is not listed; loading it for a backtest reports the exact problem.
            }
        }

        return models;
    }
}
