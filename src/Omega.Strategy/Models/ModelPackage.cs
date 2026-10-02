using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Omega.Core.Results;
using Omega.Strategy.Calibration;

namespace Omega.Strategy.Models;

/// <summary>Produces the raw P(TP_FIRST) from a feature vector.</summary>
public interface IProbabilityModel
{
    string ModelId { get; }

    /// <summary>Feature names, in the order the model expects them.</summary>
    IReadOnlyList<string> Features { get; }

    double PredictRaw(IReadOnlyDictionary<string, double?> features);
}

public static class ModelPackageErrors
{
    public const string NotFound = "MODEL_NOT_FOUND";
    public const string Invalid = "MODEL_PACKAGE_INVALID";
    public const string Incomplete = "MODEL_PACKAGE_INCOMPLETE";
}

/// <summary>
/// A registered model as produced by the research pipeline: <c>model.onnx</c>, <c>manifest.json</c>,
/// <c>calibration.json</c> (Phase 8) and <c>outcome_profile.json</c> (Phase 9). Loading verifies the ONNX file
/// against the manifest hash, so a modified model never runs.
/// </summary>
public sealed class ModelPackage : IProbabilityModel, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly bool _doubleInput;

    private ModelPackage(
        InferenceSession session, string modelId, string modelType, IReadOnlyList<string> features, string inputName, bool doubleInput,
        string trainingPeriod, double stopAtr, double targetAtr, int horizonCandles, ProbabilityCalibrator calibrator, OutcomeProfile profile)
    {
        _session = session;
        _inputName = inputName;
        _doubleInput = doubleInput;
        ModelId = modelId;
        ModelType = modelType;
        Features = features;
        TrainingPeriod = trainingPeriod;
        StopAtr = stopAtr;
        TargetAtr = targetAtr;
        HorizonCandles = horizonCandles;
        Calibrator = calibrator;
        Profile = profile;
    }

    public string ModelId { get; }

    public string ModelType { get; }

    public IReadOnlyList<string> Features { get; }

    /// <summary>Period the model was trained on, for example <c>2025-01-01T00:00:00+00:00..2025-10-01T00:00:00+00:00</c>.</summary>
    public string TrainingPeriod { get; }

    /// <summary>Barriers of the label the model was trained on (ATR multiples and horizon).</summary>
    public double StopAtr { get; }

    public double TargetAtr { get; }

    public int HorizonCandles { get; }

    public ProbabilityCalibrator Calibrator { get; }

    public OutcomeProfile Profile { get; }

    public static Result<ModelPackage> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Fail(ModelPackageErrors.NotFound, $"Model directory '{directory}' does not exist.");
        }

        foreach (var file in new[] { "manifest.json", "model.onnx", "calibration.json", "outcome_profile.json" })
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                return Fail(ModelPackageErrors.Incomplete,
                    $"Model package is missing {file}. Run the Phase 7 experiment and the Phase 8 calibration for this model.");
            }
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            var root = manifest.RootElement;
            var bytes = File.ReadAllBytes(Path.Combine(directory, "model.onnx"));

            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), root.GetProperty("onnxSha256").GetString(), StringComparison.Ordinal))
            {
                return Fail(ModelPackageErrors.Invalid, "model.onnx does not match the hash in its manifest.");
            }

            var input = root.GetProperty("input");
            var inputType = input.GetProperty("type").GetString();
            if (inputType is not ("float64" or "float32"))
            {
                return Fail(ModelPackageErrors.Invalid, $"Unsupported input type '{inputType}'.");
            }

            var calibration = ProbabilityCalibrator.FromJson(File.ReadAllText(Path.Combine(directory, "calibration.json")));
            if (calibration.IsFailure)
            {
                return Result.Failure<ModelPackage>(calibration.Error!);
            }

            using var profileDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "outcome_profile.json")));
            var profileRoot = profileDocument.RootElement;
            if (profileRoot.GetProperty("format").GetString() != "omega-outcome-profile-v1")
            {
                return Fail(ModelPackageErrors.Invalid, "Unsupported outcome profile format.");
            }

            var profile = new OutcomeProfile(profileRoot.GetProperty("winMean").GetDouble(), profileRoot.GetProperty("lossMean").GetDouble());
            if (!double.IsFinite(profile.WinMean) || !double.IsFinite(profile.LossMean) || profile.WinMean <= 0 || profile.LossMean >= profile.WinMean)
            {
                return Fail(ModelPackageErrors.Invalid, "The outcome profile must have a positive win mean above the loss mean.");
            }

            var label = root.GetProperty("label");
            var features = input.GetProperty("features").EnumerateArray().Select(e => e.GetString()!).ToList();
            var session = new InferenceSession(bytes);

            return Result.Success(new ModelPackage(
                session,
                root.GetProperty("modelId").GetString()!,
                root.GetProperty("modelType").GetString()!,
                features,
                input.GetProperty("name").GetString()!,
                inputType == "float64",
                root.GetProperty("trainingPeriod").GetString()!,
                label.GetProperty("stopAtr").GetDouble(),
                label.GetProperty("targetAtr").GetDouble(),
                label.GetProperty("horizonCandles").GetInt32(),
                calibration.Value,
                profile));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or OnnxRuntimeException or IOException)
        {
            return Fail(ModelPackageErrors.Invalid, $"Model package could not be loaded: {ex.Message}");
        }
    }

    public double PredictRaw(IReadOnlyDictionary<string, double?> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var values = new double[Features.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = features.TryGetValue(Features[i], out var value) && value is { } v && double.IsFinite(v)
                ? v
                : throw new ArgumentException($"Feature '{Features[i]}' is missing or not finite.", nameof(features));
        }

        using var results = _session.Run([_doubleInput
            ? NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<double>(values, [1, values.Length]))
            : NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(values.Select(v => (float)v).ToArray(), [1, values.Length]))]);

        // The output element type does not always follow the input type (a random forest with float64 inputs
        // returns float32 probabilities), so it is read as whatever the model declares.
        return results.FirstOrDefault(r => r.Name == "probabilities")?.Value switch
        {
            Tensor<double> doubles => doubles[0, 1],
            Tensor<float> floats => floats[0, 1],
            _ => throw new InvalidOperationException($"Model {ModelId} has no float or double 'probabilities' output."),
        };
    }

    public void Dispose() => _session.Dispose();

    private static Result<ModelPackage> Fail(string code, string message) => Result.Failure<ModelPackage>(new Error(code, message));
}
