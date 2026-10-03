using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Application.Monitoring;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Core.SystemEvents;

namespace Omega.Api.Endpoints;

/// <summary>A system event as the dashboard's log panel shows it.</summary>
public sealed record SystemEventResponse(DateTimeOffset OccurredAtUtc, string Source, string EventType, string Severity, string Message, IReadOnlyDictionary<string, string> Details);

/// <summary>Read-only endpoints the monitoring dashboard needs (Phase 13): recent candles, system health and events.</summary>
public static partial class MonitoringEndpoints
{
    public const int MaxCandles = 1_000;

    public static IEndpointRouteBuilder MapMonitoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/market/{symbol}/{interval}/candles", CandlesAsync);
        endpoints.MapGet("/api/system/health", HealthAsync);
        endpoints.MapGet("/api/system/events", EventsAsync);
        return endpoints;
    }

    /// <summary>The latest <paramref name="limit"/> stored closed candles, oldest first.</summary>
    public static async Task<Results<Ok<IReadOnlyList<CandleResponse>>, ValidationProblem, ProblemHttpResult>> CandlesAsync(
        string symbol, string interval, ICandleStore candles, CancellationToken cancellationToken, int limit = 150)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var errors = Validate(symbol, interval, out var candleInterval);
        if (limit is < 1 or > MaxCandles) errors["limit"] = [$"Between 1 and {MaxCandles}."];
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            if (await candles.GetLatestAsync(symbol, candleInterval, cancellationToken) is not { } latest)
            {
                return TypedResults.Ok<IReadOnlyList<CandleResponse>>([]);
            }

            var length = candleInterval.ToTimeSpan();
            var range = await candles.GetRangeAsync(symbol, candleInterval, latest.OpenTimeUtc - (length * (limit - 1)), latest.OpenTimeUtc + length, cancellationToken);
            return TypedResults.Ok<IReadOnlyList<CandleResponse>>([.. range.Select(CandleResponse.From)]);
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Market data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Health of the database, market data, paper worker and model. Always 200: an unhealthy system is data, not an error.</summary>
    public static async Task<Results<Ok<SystemHealth>, ValidationProblem>> HealthAsync(
        SystemHealthService health, CancellationToken cancellationToken, string symbol = "BTCUSDT", string interval = "5m")
    {
        ArgumentNullException.ThrowIfNull(health);
        var errors = Validate(symbol, interval, out var candleInterval);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return TypedResults.Ok(await health.CheckAsync(symbol, candleInterval, cancellationToken));
    }

    public static async Task<Results<Ok<IReadOnlyList<SystemEventResponse>>, ValidationProblem, ProblemHttpResult>> EventsAsync(
        ISystemEventStore events, CancellationToken cancellationToken, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (limit is < 1 or > 500)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = ["Between 1 and 500."] });
        }

        try
        {
            return TypedResults.Ok<IReadOnlyList<SystemEventResponse>>([.. (await events.GetRecentAsync(limit, cancellationToken))
                .Select(e => new SystemEventResponse(e.OccurredAtUtc, e.Source, e.EventType, e.Severity.ToString(), e.Message, e.Details))]);
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Event store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Dictionary<string, string[]> Validate(string symbol, string interval, out CandleInterval candleInterval)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(symbol) || !SymbolPattern().IsMatch(symbol)) errors["symbol"] = ["2-20 upper-case letters or digits, for example BTCUSDT."];
        if (!CandleIntervalExtensions.TryParseCode(interval, out candleInterval)) errors["interval"] = ["Supported: 5m."];
        return errors;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z0-9]{2,20}$")]
    private static partial System.Text.RegularExpressions.Regex SymbolPattern();
}
