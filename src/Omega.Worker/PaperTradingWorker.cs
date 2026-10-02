using Microsoft.Extensions.Options;
using Omega.Application.Paper;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Execution.Paper;
using Omega.Features;
using Omega.MarketData.Configuration;
using Omega.Risk;

namespace Omega.Worker;

/// <summary>
/// Runs the paper-trading pipeline (Phase 12) on every new closed candle and applies operator commands. Registered
/// only when Trading:Mode is Paper. A session whose configuration changed, or whose model cannot be loaded, stops the
/// worker instead of trading with something nobody configured.
/// </summary>
public sealed partial class PaperTradingWorker(
    IPaperTradingStore store,
    ICandleStore candles,
    FeatureEngine featureEngine,
    IOptions<PaperTradingOptions> paperOptions,
    IOptions<MarketDataOptions> marketData,
    RiskLimits riskLimits,
    ResearchModelsDirectory models,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<PaperTradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = paperOptions.Value;
        var strategy = PaperStrategyFactory.Create(options, models.Path);
        if (strategy.IsFailure)
        {
            LogRefused(logger, strategy.Error!.Message);
            StopWithError();
            return;
        }

        using var resource = strategy.Value.Resource;
        var engine = new PaperTradingEngine(store, candles, featureEngine, strategy.Value.Strategy, new PaperSessionConfig
        {
            Name = options.Session,
            Symbol = marketData.Value.Symbol,
            Interval = marketData.Value.Interval,
            Strategy = strategy.Value.Strategy.Identity,
            InitialCapital = options.InitialCapital,
            Costs = options.Costs,
            Risk = riskLimits,
            MaxHoldingCandles = strategy.Value.MaxHoldingCandles,
            FreshnessGrace = options.FreshnessGrace,
        }, time, logger);

        var started = await StartWithRetriesAsync(engine, options, stoppingToken).ConfigureAwait(false);
        if (!started)
        {
            StopWithError();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.ApplyCommandsAsync(stoppingToken).ConfigureAwait(false);
                var processed = await engine.ProcessNewCandlesAsync(stoppingToken).ConfigureAwait(false);
                if (processed > 0)
                {
                    LogProcessed(logger, processed, options.Session, engine.Risk.KillSwitch.IsActive);
                }
            }
            catch (PersistenceException ex)
            {
                // Nothing of the failed candle was saved (one transaction per candle): it is processed again next cycle.
                LogStoreError(logger, ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(options.PollInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>A refusal must not look like a clean exit to a process supervisor.</summary>
    private void StopWithError()
    {
        Environment.ExitCode = RefusedExitCode;
        lifetime.StopApplication();
    }

    /// <summary>Exit code when paper trading refuses to start (configuration changed, model unavailable).</summary>
    public const int RefusedExitCode = 3;

    private async Task<bool> StartWithRetriesAsync(PaperTradingEngine engine, PaperTradingOptions options, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await engine.StartAsync(stoppingToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    LogRefused(logger, result.Error!.Message);
                    return false;
                }

                LogStarted(logger, options.Session, options.Strategy);
                return true;
            }
            catch (PersistenceException ex)
            {
                LogStoreError(logger, ex.Message);
                await Task.Delay(options.PollInterval, time, stoppingToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Paper trading refused to start: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paper trading session {Session} running strategy {Strategy}.")]
    private static partial void LogStarted(ILogger logger, string session, string strategy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paper trading processed {Count} candle(s) in session {Session}; kill switch active: {KillSwitch}.")]
    private static partial void LogProcessed(ILogger logger, int count, string session, bool killSwitch);

    [LoggerMessage(Level = LogLevel.Error, Message = "Paper trading store unavailable; retrying: {Reason}")]
    private static partial void LogStoreError(ILogger logger, string reason);
}

/// <summary>Where registered model packages live (Research:ModelsDirectory).</summary>
public sealed record ResearchModelsDirectory(string? Path);
