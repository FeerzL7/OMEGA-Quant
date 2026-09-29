using Microsoft.Extensions.Logging;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Core.SystemEvents;

namespace Omega.Application.MarketData;

/// <summary>
/// Periodically evaluates the <see cref="MarketState"/> and records transitions
/// between fresh and stale data as system events (once per transition, not per check).
/// </summary>
public sealed partial class MarketDataFreshnessMonitor(
    MarketStateService marketState,
    ISystemEventStore systemEvents,
    MarketStateOptions options,
    TimeProvider timeProvider,
    ILogger<MarketDataFreshnessMonitor> logger)
{
    public const string EventSource = "MarketDataFreshnessMonitor";

    /// <summary>Runs until cancelled; returns normally on cancellation.</summary>
    public async Task RunAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        DataFreshness? previous = null;
        using var timer = new PeriodicTimer(options.EvaluationInterval, timeProvider);

        try
        {
            do
            {
                previous = await EvaluateAsync(symbol, interval, previous, cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One evaluation; returns the freshness to compare against next time.</summary>
    internal async Task<DataFreshness?> EvaluateAsync(
        string symbol, CandleInterval interval, DataFreshness? previous, CancellationToken cancellationToken)
    {
        MarketState state;
        try
        {
            state = await marketState.GetStateAsync(symbol, interval, cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogEvaluationFailed(logger, ex, symbol);
            return previous;
        }

        if (state.Freshness == previous)
        {
            return previous;
        }

        LogFreshnessChanged(logger, symbol, interval, previous, state.Freshness, state.DataAge?.TotalSeconds);

        // The first evaluation only records a problem, not "data is fresh" (nothing changed yet).
        var systemEvent = state.Freshness switch
        {
            DataFreshness.Stale => new SystemEvent(
                state.EvaluatedAtUtc, EventSource, SystemEventTypes.MarketDataStale, SystemEventSeverity.Warning,
                $"{symbol} {interval} market data is stale: next candle expected at {state.NextCloseExpectedUtc:O} has not been stored.",
                Details(state)),
            DataFreshness.Fresh when previous is not null => new SystemEvent(
                state.EvaluatedAtUtc, EventSource, SystemEventTypes.MarketDataFresh, SystemEventSeverity.Information,
                $"{symbol} {interval} market data is fresh again.",
                Details(state)),
            _ => null,
        };

        if (systemEvent is not null)
        {
            try
            {
                await systemEvents.AppendAsync(systemEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (PersistenceException ex)
            {
                LogEventNotStored(logger, ex, systemEvent.EventType);
            }
        }

        return state.Freshness;
    }

    private static Dictionary<string, string> Details(MarketState state) => new(StringComparer.Ordinal)
    {
        ["symbol"] = state.Symbol,
        ["interval"] = state.Interval.ToCode(),
        ["lastCloseTimeUtc"] = state.LastClosedCandle?.CloseTimeUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "none",
        ["dataAgeSeconds"] = state.DataAge?.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "none",
    };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{Symbol} {Interval} freshness {Previous} -> {Current} (data age {DataAgeSeconds:0} s).")]
    private static partial void LogFreshnessChanged(
        ILogger logger, string symbol, CandleInterval interval, DataFreshness? previous, DataFreshness current, double? dataAgeSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not evaluate {Symbol} market state.")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception exception, string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "System event {EventType} could not be stored; it is only in the logs.")]
    private static partial void LogEventNotStored(ILogger logger, Exception exception, string eventType);
}
