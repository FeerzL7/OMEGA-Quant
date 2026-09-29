using Microsoft.Extensions.Options;
using Omega.Core.Configuration;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.MarketData;

namespace Omega.Worker;

/// <summary>
/// Phase 1 consumer of the market-data stream: it only records closed candles,
/// gaps and connection changes as structured logs. Persistence arrives in
/// Phase 2; no candle reaches strategy, risk or execution code yet.
/// </summary>
public sealed partial class MarketDataWorker(
    IMarketDataStream stream,
    IOptions<TradingOptions> tradingOptions,
    ILogger<MarketDataWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, tradingOptions.Value.Mode);

        await foreach (var marketDataEvent in stream.ReadEventsAsync(stoppingToken))
        {
            switch (marketDataEvent)
            {
                case CandleClosedEvent { Candle: var candle } closed:
                    LogCandleClosed(
                        logger, candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                        candle.Open, candle.High, candle.Low, candle.Close, candle.BaseVolume, candle.TradeCount,
                        (closed.ObservedAtUtc - candle.CloseTimeUtc).TotalMilliseconds);
                    break;

                case DataGapDetectedEvent gap:
                    LogGap(logger, gap.Symbol, gap.Interval, gap.MissingCandles, gap.FirstMissingOpenTimeUtc);
                    break;

                case ConnectionStatusChangedEvent connection:
                    LogConnection(logger, connection.Status, connection.Reason);
                    break;
            }
        }

        LogStopped(logger);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OMEGA worker started. Trading mode: {TradingMode}. Streaming market data only (Phase 1).")]
    private static partial void LogStarted(ILogger logger, TradingMode tradingMode);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Candle closed {Symbol} {Interval} {OpenTimeUtc:O}-{CloseTimeUtc:O} O={Open} H={High} L={Low} C={Close} V={BaseVolume} Trades={TradeCount} LatencyMs={LatencyMs:0}")]
    private static partial void LogCandleClosed(
        ILogger logger, string symbol, CandleInterval interval, DateTimeOffset openTimeUtc, DateTimeOffset closeTimeUtc,
        decimal open, decimal high, decimal low, decimal close, decimal baseVolume, long tradeCount, double latencyMs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Market-data gap: {MissingCandles} {Symbol} {Interval} candle(s) missing from {FirstMissingOpenTimeUtc:O}. Data integrity is not guaranteed for this period.")]
    private static partial void LogGap(
        ILogger logger, string symbol, CandleInterval interval, int missingCandles, DateTimeOffset firstMissingOpenTimeUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Market-data connection {Status}: {Reason}")]
    private static partial void LogConnection(ILogger logger, ConnectionStatus status, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "OMEGA worker stopped.")]
    private static partial void LogStopped(ILogger logger);
}
