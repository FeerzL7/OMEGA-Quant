using Omega.Api.Endpoints;

namespace Omega.Api.Tests.Endpoints;

public class ModelEndpointsTests
{
    [Fact]
    public void Models_are_listed_from_the_configured_directory()
    {
        var root = Directory.CreateTempSubdirectory("omega-api-models-").FullName;
        var model = Path.Combine(root, "logistic_regression-abc");
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(model, "manifest.json"),
            """{"modelId":"logistic_regression-abc","modelType":"logistic_regression","trainingPeriod":"a..b","datasetId":"d"}""");

        var models = ModelEndpoints.List(new ResearchPaths(Path.GetTempPath(), root)).Value!;

        var summary = Assert.Single(models);
        Assert.Equal("logistic_regression-abc", summary.ModelId);
        Assert.Null(summary.CalibrationMethod);
        Assert.False(summary.ReadyForExpectedValue);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Strategies_include_the_model_driven_strategy()
    {
        var strategies = BacktestEndpoints.GetStrategies().Value!;

        var model = Assert.Single(strategies, s => s.Name == "model-ev");
        Assert.True(model.RequiresModelId);
    }
}
