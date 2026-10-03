using System.Globalization;
using System.Text.Json;
using Omega.Application.MarketData;
using Omega.Application.Research;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Execution.Paper;

namespace Omega.Application.Monitoring;

/// <summary>State of a component as the dashboard shows it. NotAvailable is the zero value: never mistaken for healthy.</summary>
public enum ComponentState
{
    NotAvailable = 0,
    Healthy = 1,
    Degraded = 2,
    Down = 3,
}

/// <summary>One component's health.</summary>
/// <param name="Name">database, marketData, paperWorker, model.</param>
/// <param name="State">The judgement.</param>
/// <param name="Detail">What was observed, in plain language.</param>
/// <param name="Basis">How the judgement was made (the API cannot see every process directly; it says so).</param>
public sealed record ComponentHealth(string Name, ComponentState State, string Detail, string Basis);

public sealed record SystemHealth(DateTimeOffset CheckedAtUtc, IReadOnlyList<ComponentHealth> Components);

/// <summary>
/// Health of the parts the dashboard depends on (CLAUDE.md §5 System). Each judgement states its basis: the worker and
/// the exchange connection live in another process, so their health is inferred from the data they leave behind.
/// </summary>
public sealed class SystemHealthService(
    ICandleStore candles, MarketStateService marketState, IPaperTradingStore paper, string? modelsDirectory, TimeProvider timeProvider)
{
    public async Task<SystemHealth> CheckAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var components = new List<ComponentHealth>();

        // Database: a real query. If it fails, nothing else that reads it can be judged.
        try
        {
            await candles.GetLatestAsync(symbol, interval, cancellationToken).ConfigureAwait(false);
            components.Add(new ComponentHealth("database", ComponentState.Healthy, "Query succeeded.", "A query to the candle store."));
        }
        catch (PersistenceException ex)
        {
            components.Add(new ComponentHealth("database", ComponentState.Down, "Unavailable: " + ex.Message, "A query to the candle store."));
            components.Add(Unknown("marketData"));
            components.Add(Unknown("paperWorker"));
            components.Add(Unknown("model"));
            return new SystemHealth(now, components);
        }

        var state = await marketState.GetStateAsync(symbol, interval, cancellationToken).ConfigureAwait(false);
        components.Add(new ComponentHealth(
            "marketData",
            state.LastClosedCandle is null ? ComponentState.NotAvailable : state.IsReliable ? ComponentState.Healthy : ComponentState.Degraded,
            state.LastClosedCandle is null
                ? "No candle stored yet."
                : Text($"{state.Freshness}; last closed candle {state.LastClosedCandle.OpenTimeUtc:O}; {(state.Issues.Count == 0 ? "no issues" : string.Join(" ", state.Issues))}"),
            "Freshness and gaps of the stored candles (written by the worker's Binance ingestion)."));

        var sessions = await paper.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        components.Add(PaperWorker(sessions, interval, now));
        components.Add(Model(sessions));

        return new SystemHealth(now, components);
    }

    private static ComponentHealth PaperWorker(IReadOnlyList<PaperSessionRecord> sessions, CandleInterval interval, DateTimeOffset now)
    {
        const string basis = "Age of the last update of the most recently active paper session (updated on every candle and command).";
        if (sessions.Count == 0)
        {
            return new ComponentHealth("paperWorker", ComponentState.NotAvailable, "No paper session exists (Trading:Mode is not Paper, or it never ran).", basis);
        }

        var latest = sessions.MaxBy(s => s.UpdatedAtUtc)!;
        var age = now - latest.UpdatedAtUtc;
        var expected = interval.ToTimeSpan();
        var state = age <= expected * 2 ? ComponentState.Healthy : age <= expected * 6 ? ComponentState.Degraded : ComponentState.Down;
        return new ComponentHealth("paperWorker", state, Text($"Session '{latest.Name}' last updated {age.TotalMinutes:0.0} min ago."), basis);
    }

    private ComponentHealth Model(IReadOnlyList<PaperSessionRecord> sessions)
    {
        const string basis = "Files of the model package used by a paper session (model, calibration, outcome profile).";
        var modelIds = sessions
            .Select(s => ModelIdOf(s.ConfigJson))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (modelIds.Count == 0)
        {
            return new ComponentHealth("model", ComponentState.NotAvailable, "No paper session uses a model (the baseline strategy needs none).", basis);
        }

        var registered = modelsDirectory is null ? [] : ModelRegistry.List(modelsDirectory);
        var missing = modelIds.Where(id => !registered.Any(m => m.ModelId == id && m.ReadyForExpectedValue)).ToList();
        return missing.Count == 0
            ? new ComponentHealth("model", ComponentState.Healthy, "Ready: " + string.Join(", ", modelIds), basis)
            : new ComponentHealth("model", ComponentState.Down, "Missing or incomplete: " + string.Join(", ", missing), basis);
    }

    /// <summary>The model id of a model-ev session, from its stored strategy parameters.</summary>
    private static string? ModelIdOf(string configJson)
    {
        using var config = JsonDocument.Parse(configJson);
        return config.RootElement.TryGetProperty("strategy", out var strategy)
            && strategy.TryGetProperty("parameters", out var parameters)
            && parameters.TryGetProperty("modelId", out var modelId)
            ? modelId.GetString()
            : null;
    }

    private static ComponentHealth Unknown(string name) =>
        new(name, ComponentState.NotAvailable, "Cannot be judged while the database is unavailable.", "Depends on the database.");

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
