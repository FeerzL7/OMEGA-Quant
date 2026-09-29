using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Application.MarketData;
using Omega.Core.MarketData;
using Omega.Core.Persistence;

namespace Omega.Api.Endpoints;

/// <summary>Read-only market-data endpoints.</summary>
public static partial class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/market");
        group.MapGet("/{symbol}/{interval}/state", GetStateAsync);
        return endpoints;
    }

    /// <summary>
    /// <c>GET /api/market/{symbol}/{interval}/state</c>, for example <c>/api/market/BTCUSDT/5m/state</c>.
    /// </summary>
    public static async Task<Results<Ok<MarketStateResponse>, ValidationProblem, ProblemHttpResult>> GetStateAsync(
        string symbol, string interval, MarketStateService marketState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marketState);

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
            var state = await marketState.GetStateAsync(symbol, candleInterval, cancellationToken);
            return TypedResults.Ok(MarketStateResponse.From(state));
        }
        catch (PersistenceException)
        {
            // Internal details (hosts, driver messages) are never returned to clients; they are in the logs.
            return TypedResults.Problem(
                title: "Market data store unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z0-9]{2,20}$")]
    private static partial System.Text.RegularExpressions.Regex SymbolPattern();
}
