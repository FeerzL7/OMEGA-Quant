using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Risk;

namespace Omega.Api.Endpoints;

/// <summary>Risk policy in force (section <c>Risk</c> of the configuration). Read-only.</summary>
public static class RiskEndpoints
{
    public static IEndpointRouteBuilder MapRiskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/risk/limits", (RiskLimits limits) => GetLimits(limits));
        return endpoints;
    }

    public static Ok<RiskLimits> GetLimits(RiskLimits limits) => TypedResults.Ok(limits);
}
