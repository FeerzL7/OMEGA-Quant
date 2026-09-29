using Microsoft.Extensions.Options;
using Omega.Api.Contracts;
using Omega.Core.Configuration;

namespace Omega.Api.Endpoints;

/// <summary>
/// System-level, read-only endpoints. Future application endpoints (market,
/// signals, risk, backtests) will live in their own files in this folder.
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/system");

        group.MapGet("/status", (IOptions<TradingOptions> tradingOptions, TimeProvider timeProvider) =>
            TypedResults.Ok(SystemStatusResponse.Create(tradingOptions.Value, timeProvider)));

        return endpoints;
    }
}
