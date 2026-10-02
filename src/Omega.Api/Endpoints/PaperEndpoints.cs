using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Application.Paper;
using Omega.Core.Persistence;
using Omega.Execution;
using Omega.Execution.Paper;

namespace Omega.Api.Endpoints;

/// <summary>Body of <c>POST /api/paper/sessions/{name}/kill-switch</c>.</summary>
/// <param name="Action">trip or reset.</param>
/// <param name="Reason">Why (required, recorded).</param>
/// <param name="RequestedBy">Who (required, recorded).</param>
public sealed record KillSwitchRequest(string? Action, string? Reason, string? RequestedBy);

public sealed record KillSwitchAccepted(long CommandId, string Session, string Action, string Note);

public sealed record PaperOrderView(Order Order, IReadOnlyList<OrderEvent> Events);

/// <summary>
/// Paper trading (Phase 12): read-only views of the sessions run by the worker, and the kill-switch command. The API
/// never changes trading state itself: commands are queued and applied by the worker, which owns the session.
/// </summary>
public static class PaperEndpoints
{
    public static IEndpointRouteBuilder MapPaperEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/paper/sessions");
        group.MapGet("/", ListAsync);
        group.MapGet("/{name}", GetAsync);
        group.MapGet("/{name}/trades", TradesAsync);
        group.MapGet("/{name}/decisions", DecisionsAsync);
        group.MapGet("/{name}/orders", OrdersAsync);
        group.MapGet("/{name}/commands", CommandsAsync);
        group.MapPost("/{name}/kill-switch", KillSwitchAsync);
        return endpoints;
    }

    public static async Task<Results<Ok<IReadOnlyList<PaperSessionSummary>>, ProblemHttpResult>> ListAsync(IPaperTradingStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return TypedResults.Ok<IReadOnlyList<PaperSessionSummary>>([.. (await store.ListSessionsAsync(cancellationToken)).Select(PaperSessions.Describe)]);
        }
        catch (PersistenceException)
        {
            return Unavailable();
        }
    }

    public static async Task<Results<Ok<PaperSessionSummary>, NotFound, ProblemHttpResult>> GetAsync(string name, IPaperTradingStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return await store.GetSessionAsync(name, cancellationToken) is { } session ? TypedResults.Ok(PaperSessions.Describe(session)) : TypedResults.NotFound();
        }
        catch (PersistenceException)
        {
            return Unavailable();
        }
    }

    public static Task<Results<Ok<IReadOnlyList<PaperTrade>>, NotFound, ProblemHttpResult>> TradesAsync(
        string name, IPaperTradingStore store, CancellationToken cancellationToken, int limit = 100) =>
        WithSessionAsync(name, store, id => store.GetTradesAsync(id, Math.Clamp(limit, 1, 1_000), cancellationToken), cancellationToken);

    public static Task<Results<Ok<IReadOnlyList<PaperDecision>>, NotFound, ProblemHttpResult>> DecisionsAsync(
        string name, IPaperTradingStore store, CancellationToken cancellationToken, int limit = 100) =>
        WithSessionAsync(name, store, id => store.GetDecisionsAsync(id, Math.Clamp(limit, 1, 1_000), cancellationToken), cancellationToken);

    public static Task<Results<Ok<IReadOnlyList<PaperCommand>>, NotFound, ProblemHttpResult>> CommandsAsync(
        string name, IPaperTradingStore store, CancellationToken cancellationToken, int limit = 50) =>
        WithSessionAsync(name, store, id => store.GetCommandsAsync(id, Math.Clamp(limit, 1, 500), cancellationToken), cancellationToken);

    public static Task<Results<Ok<IReadOnlyList<PaperOrderView>>, NotFound, ProblemHttpResult>> OrdersAsync(
        string name, IPaperTradingStore store, CancellationToken cancellationToken, int limit = 50) =>
        WithSessionAsync<IReadOnlyList<PaperOrderView>>(name, store, async id =>
        {
            var views = new List<PaperOrderView>();
            foreach (var order in await store.GetOrdersAsync(id, Math.Clamp(limit, 1, 500), cancellationToken))
            {
                views.Add(new PaperOrderView(order, await store.GetOrderEventsAsync(order.Id, cancellationToken)));
            }

            return views;
        }, cancellationToken);

    public static async Task<Results<Accepted<KillSwitchAccepted>, NotFound, ValidationProblem, ProblemHttpResult>> KillSwitchAsync(
        string name, KillSwitchRequest request, IPaperTradingStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Action is not ("trip" or "reset")) errors["action"] = ["trip or reset."];
        if (string.IsNullOrWhiteSpace(request.Reason)) errors["reason"] = ["Required: every kill-switch change is audited."];
        if (string.IsNullOrWhiteSpace(request.RequestedBy)) errors["requestedBy"] = ["Required: every kill-switch change is audited."];
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            if (await store.GetSessionAsync(name, cancellationToken) is not { } session)
            {
                return TypedResults.NotFound();
            }

            var type = request.Action == "trip" ? PaperCommandType.TripKillSwitch : PaperCommandType.ResetKillSwitch;
            var id = await store.EnqueueCommandAsync(session.Id, type, request.Reason!.Trim(), request.RequestedBy!.Trim(), time.GetUtcNow(), cancellationToken);
            return TypedResults.Accepted($"/api/paper/sessions/{name}/commands",
                new KillSwitchAccepted(id, name, request.Action!, "Queued: the paper worker applies it within its poll interval. Check GET .../commands for the result."));
        }
        catch (PersistenceException)
        {
            return Unavailable();
        }
    }

    private static async Task<Results<Ok<T>, NotFound, ProblemHttpResult>> WithSessionAsync<T>(
        string name, IPaperTradingStore store, Func<Guid, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return await store.GetSessionAsync(name, cancellationToken) is { } session ? TypedResults.Ok(await read(session.Id)) : TypedResults.NotFound();
        }
        catch (PersistenceException)
        {
            return Unavailable();
        }
    }

    private static ProblemHttpResult Unavailable() =>
        TypedResults.Problem(title: "Data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
}
