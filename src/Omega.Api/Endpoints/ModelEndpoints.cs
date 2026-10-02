using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Application.Research;

namespace Omega.Api.Endpoints;

/// <summary>Registered model packages (research models directory). Read-only.</summary>
public static class ModelEndpoints
{
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/models", List);
        return endpoints;
    }

    public static Ok<IReadOnlyList<ModelSummary>> List(ResearchPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return TypedResults.Ok(ModelRegistry.List(paths.ModelsDirectory));
    }
}
