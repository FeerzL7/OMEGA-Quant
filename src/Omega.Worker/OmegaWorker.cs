using Microsoft.Extensions.Options;
using Omega.Core.Configuration;
using Omega.Core.Trading;

namespace Omega.Worker;

/// <summary>
/// Background host for the future pipeline stages (market data, features,
/// strategy evaluation, paper trading, monitoring).
/// Phase 0: no stage is registered. The worker only honours the host lifetime
/// and shuts down cleanly when cancellation is requested.
/// </summary>
public sealed partial class OmegaWorker(ILogger<OmegaWorker> logger, IOptions<TradingOptions> tradingOptions)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, tradingOptions.Value.Mode);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown.
        }

        LogStopped(logger);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OMEGA worker started. Trading mode: {TradingMode}. No pipeline stages are registered (Phase 0).")]
    private static partial void LogStarted(ILogger logger, TradingMode tradingMode);

    [LoggerMessage(Level = LogLevel.Information, Message = "OMEGA worker stopped.")]
    private static partial void LogStopped(ILogger logger);
}
