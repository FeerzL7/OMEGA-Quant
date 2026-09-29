using Microsoft.Extensions.Options;
using Omega.Application.MarketData;
using Omega.Core.Configuration;
using Omega.Core.Trading;

namespace Omega.Worker;

/// <summary>
/// Hosts the market-data ingestion (stream → PostgreSQL). No candle reaches
/// strategy, risk or execution code yet.
/// </summary>
public sealed partial class MarketDataIngestionWorker(
    MarketDataIngestionService ingestion,
    IOptions<TradingOptions> tradingOptions,
    ILogger<MarketDataIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, tradingOptions.Value.Mode);
        await ingestion.RunAsync(stoppingToken);
        LogStopped(logger);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OMEGA worker started. Trading mode: {TradingMode}. Ingesting market data only (Phase 3).")]
    private static partial void LogStarted(ILogger logger, TradingMode tradingMode);

    [LoggerMessage(Level = LogLevel.Information, Message = "OMEGA worker stopped.")]
    private static partial void LogStopped(ILogger logger);
}

/// <summary>Hosts the freshness monitor for the configured symbol and interval.</summary>
public sealed class MarketDataFreshnessWorker(
    MarketDataFreshnessMonitor monitor,
    IOptions<Omega.MarketData.Configuration.MarketDataOptions> marketData) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        monitor.RunAsync(marketData.Value.Symbol, marketData.Value.Interval, stoppingToken);
}
