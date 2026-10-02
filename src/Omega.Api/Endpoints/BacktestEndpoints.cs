using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Application.Backtesting;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Persistence;

namespace Omega.Api.Endpoints;

/// <summary>
/// Backtests: a controlled research command. It only reads stored candles and writes an experiment record;
/// it never touches an exchange or places orders.
/// </summary>
public static partial class BacktestEndpoints
{
    public static IEndpointRouteBuilder MapBacktestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/strategies", GetStrategies);
        var group = endpoints.MapGroup("/api/backtests");
        group.MapPost("/", RunAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/monte-carlo", MonteCarloAsync);
        return endpoints;
    }

    public static Ok<IReadOnlyList<StrategyResponse>> GetStrategies() =>
        TypedResults.Ok<IReadOnlyList<StrategyResponse>>(
        [
            .. StrategyCatalog.All.Select(d => new StrategyResponse(d.Name, d.Description, d.DefaultConfig, d.IsBenchmark)),
            new StrategyResponse(
                BacktestService.ModelStrategy,
                "Model-driven: calibrated P(TP_FIRST) from a registered model → expected value after costs; long only if EV > minExpectedReturn (default 0). Uses the model's label barriers and horizon. Requires modelId.",
                new BacktestConfig(),
                IsBenchmark: false,
                RequiresModelId: true),
        ]);

    public static async Task<Results<Created<BacktestRunResponse>, ValidationProblem, ProblemHttpResult>> RunAsync(
        BacktestRunRequest request, BacktestService backtests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(backtests);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Strategy)) errors["strategy"] = ["Required."];
        if (request.Symbol is null || !SymbolPattern().IsMatch(request.Symbol)) errors["symbol"] = ["2-20 upper-case letters or digits, for example BTCUSDT."];
        if (!CandleIntervalExtensions.TryParseCode(request.Interval, out var interval)) errors["interval"] = ["Supported: 5m."];
        if (request.FromUtc is null) errors["fromUtc"] = ["Required."];
        if (request.ToUtc is null) errors["toUtc"] = ["Required."];
        if (request.PeriodLabel is null || !LabelPattern().IsMatch(request.PeriodLabel)) errors["periodLabel"] = ["Lower-case words separated by hyphens, for example 'development' or 'holdout'."];

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var serviceRequest = new BacktestRequest(
            request.Strategy!, request.Symbol!, interval, request.FromUtc!.Value.ToUniversalTime(), request.ToUtc!.Value.ToUniversalTime(),
            request.PeriodLabel!, request.FeeRate, request.SpreadBps, request.SlippageBps, request.ModelId, request.MinExpectedReturn, request.Risk);

        try
        {
            var outcome = await backtests.RunAsync(serviceRequest, cancellationToken);
            if (outcome.IsFailure)
            {
                return outcome.Error!.Code switch
                {
                    BacktestServiceErrors.NotEnoughData => TypedResults.Problem(title: outcome.Error.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
                    BacktestServiceErrors.ModelUnavailable => TypedResults.Problem(title: outcome.Error.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [outcome.Error.Code] = [outcome.Error.Message] }),
                };
            }

            var run = outcome.Value.Run;
            return TypedResults.Created($"/api/backtests/{run.Id}", BacktestRunResponse.From(run, outcome.Value.Notices));
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public static async Task<Results<Ok<IReadOnlyList<BacktestRunSummary>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        IBacktestRunStore runs, CancellationToken cancellationToken, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(runs);

        if (limit is < 1 or > 500)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = ["Between 1 and 500."] });
        }

        try
        {
            return TypedResults.Ok(await runs.ListAsync(limit, cancellationToken));
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public static async Task<Results<Ok<BacktestRunResponse>, NotFound, ProblemHttpResult>> GetAsync(
        Guid id, IBacktestRunStore runs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runs);

        try
        {
            var run = await runs.GetAsync(id, cancellationToken);
            return run is null ? TypedResults.NotFound() : TypedResults.Ok(BacktestRunResponse.From(run));
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Scenario analysis over the trades of a stored backtest (Phase 11). Not a forecast.</summary>
    public static async Task<Results<Ok<MonteCarloOutcome>, NotFound, ValidationProblem, ProblemHttpResult>> MonteCarloAsync(
        Guid id, MonteCarloRequest? request, MonteCarloService monteCarlo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monteCarlo);
        request ??= new MonteCarloRequest();

        Omega.Backtesting.MonteCarlo.MonteCarloMethod method;
        switch (request.Method)
        {
            case null or "bootstrap": method = Omega.Backtesting.MonteCarlo.MonteCarloMethod.Bootstrap; break;
            case "block-bootstrap": method = Omega.Backtesting.MonteCarlo.MonteCarloMethod.BlockBootstrap; break;
            case "shuffle": method = Omega.Backtesting.MonteCarlo.MonteCarloMethod.Shuffle; break;
            default:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["method"] = ["bootstrap, block-bootstrap or shuffle."] });
        }

        var defaults = new Omega.Backtesting.MonteCarlo.MonteCarloOptions();
        var options = defaults with
        {
            Method = method,
            Paths = request.Paths ?? defaults.Paths,
            HorizonTrades = request.HorizonTrades,
            BlockLength = request.BlockLength ?? defaults.BlockLength,
            RuinLevel = request.RuinLevel ?? defaults.RuinLevel,
            ExtraCostBps = request.ExtraCostBps ?? defaults.ExtraCostBps,
            SkipProbability = request.SkipProbability ?? defaults.SkipProbability,
            Seed = request.Seed ?? defaults.Seed,
        };

        try
        {
            var outcome = await monteCarlo.RunAsync(id, options, cancellationToken);
            if (outcome.IsSuccess)
            {
                return TypedResults.Ok(outcome.Value);
            }

            return outcome.Error!.Code switch
            {
                MonteCarloServiceErrors.RunNotFound => TypedResults.NotFound(),
                Omega.Backtesting.MonteCarlo.MonteCarloErrors.NoTrades => TypedResults.Problem(title: outcome.Error.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
                _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [outcome.Error.Code] = [outcome.Error.Message] }),
            };
        }
        catch (PersistenceException)
        {
            return TypedResults.Problem(title: "Data store unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [GeneratedRegex("^[A-Z0-9]{2,20}$")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex LabelPattern();
}
