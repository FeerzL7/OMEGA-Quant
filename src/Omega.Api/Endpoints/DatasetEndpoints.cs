using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Application.Research;
using Omega.Core.MarketData;
using Omega.Core.Persistence;

namespace Omega.Api.Endpoints;

/// <summary>Body of <c>POST /api/datasets</c>.</summary>
public sealed record DatasetExportRequest(string? Symbol, string? Interval, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc);

/// <summary>Response of <c>POST /api/datasets</c>.</summary>
public sealed record DatasetExportResponse(string DatasetId, string Directory, DatasetManifest Manifest);

/// <summary>
/// Research datasets (Phase 7): features and triple-barrier labels computed by the system itself, written to the
/// research datasets directory for the Python pipeline. Reads stored candles only.
/// </summary>
public static partial class DatasetEndpoints
{
    public static IEndpointRouteBuilder MapDatasetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/datasets", ExportAsync);
        return endpoints;
    }

    public static async Task<Results<Created<DatasetExportResponse>, ValidationProblem, ProblemHttpResult>> ExportAsync(
        DatasetExportRequest request, DatasetBuilder builder, ResearchPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(paths);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Symbol is null || !SymbolPattern().IsMatch(request.Symbol)) errors["symbol"] = ["2-20 upper-case letters or digits, for example BTCUSDT."];
        if (!CandleIntervalExtensions.TryParseCode(request.Interval, out var interval)) errors["interval"] = ["Supported: 5m."];
        if (request.FromUtc is null) errors["fromUtc"] = ["Required."];
        if (request.ToUtc is null) errors["toUtc"] = ["Required."];
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            var dataset = await builder.BuildAsync(
                new DatasetRequest(request.Symbol!, interval, request.FromUtc!.Value.ToUniversalTime(), request.ToUtc!.Value.ToUniversalTime()),
                cancellationToken);

            if (dataset.IsFailure)
            {
                return dataset.Error!.Code == DatasetErrors.NoSamples
                    ? TypedResults.Problem(title: dataset.Error.Message, statusCode: StatusCodes.Status422UnprocessableEntity)
                    : TypedResults.ValidationProblem(new Dictionary<string, string[]> { [dataset.Error.Code] = [dataset.Error.Message] });
            }

            var directory = DatasetBuilder.Write(dataset.Value, paths.DatasetsDirectory);
            return TypedResults.Created($"/api/datasets/{dataset.Value.Manifest.DatasetId}",
                new DatasetExportResponse(dataset.Value.Manifest.DatasetId, directory, dataset.Value.Manifest));
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [GeneratedRegex("^[A-Z0-9]{2,20}$")]
    private static partial Regex SymbolPattern();
}

/// <summary>Research directories. Configured by <c>Research:DatasetsDirectory</c> and <c>Research:ModelsDirectory</c>.</summary>
public sealed record ResearchPaths(string DatasetsDirectory, string ModelsDirectory);
