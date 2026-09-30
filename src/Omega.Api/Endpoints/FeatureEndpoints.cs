using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Application.Features;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Features;

namespace Omega.Api.Endpoints;

/// <summary>Read-only feature endpoints.</summary>
public static partial class FeatureEndpoints
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/features/catalog", GetCatalog);
        endpoints.MapGet("/api/market/{symbol}/{interval}/features/latest", GetLatestAsync);
        return endpoints;
    }

    /// <summary><c>GET /api/features/catalog</c>: the active feature set and every definition.</summary>
    public static Ok<FeatureCatalogResponse> GetCatalog(FeatureService features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return TypedResults.Ok(FeatureCatalogResponse.From(features.FeatureSet));
    }

    /// <summary><c>GET /api/market/{symbol}/{interval}/features/latest</c>.</summary>
    public static async Task<Results<Ok<FeatureVectorResponse>, ValidationProblem, ProblemHttpResult>> GetLatestAsync(
        string symbol, string interval, FeatureService features, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(features);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(symbol) || !SymbolPattern().IsMatch(symbol))
        {
            errors["symbol"] = ["Symbol must be 2-20 upper-case letters or digits, for example BTCUSDT."];
        }

        if (!CandleIntervalExtensions.TryParseCode(interval, out var candleInterval))
        {
            errors["interval"] = ["Unsupported interval. Supported: 5m."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            var result = await features.ComputeLatestAsync(symbol, candleInterval, cancellationToken);

            return result.IsSuccess
                ? TypedResults.Ok(FeatureVectorResponse.From(result.Value))
                : result.Error!.Code switch
                {
                    FeatureServiceErrors.NoData => TypedResults.Problem(
                        title: "No stored candles for this symbol and interval.", statusCode: StatusCodes.Status404NotFound),
                    FeatureErrors.NotContiguous => TypedResults.Problem(
                        title: "The candles needed for the features contain a gap; no features are computed from partial data.",
                        detail: result.Error.Message,
                        statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => TypedResults.Problem(title: result.Error.Message, statusCode: StatusCodes.Status500InternalServerError),
                };
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Market data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z0-9]{2,20}$")]
    private static partial System.Text.RegularExpressions.Regex SymbolPattern();
}
