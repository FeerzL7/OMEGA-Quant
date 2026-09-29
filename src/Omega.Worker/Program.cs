using Microsoft.Extensions.Options;
using Omega.Application.MarketData;
using Omega.Core.Configuration;
using Omega.Core.MarketData;
using Omega.Core.SystemEvents;
using Omega.Infrastructure.Configuration;
using Omega.MarketData;
using Omega.MarketData.Binance;
using Omega.MarketData.Configuration;
using Omega.MarketData.Transport;
using Omega.Worker;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

// Configuration (every section fails start-up with all its errors)
builder.Services.AddValidatedOptions<TradingOptions>(configuration, TradingOptions.SectionName,
    options => Enum.IsDefined(options.Mode) ? [] : ["Trading:Mode is not a valid trading mode."]);
builder.Services.AddValidatedOptions<MarketDataOptions>(configuration, MarketDataOptions.SectionName, options => options.Validate());
builder.Services.AddValidatedOptions<MarketStateOptions>(configuration, MarketStateOptions.SectionName, options => options.Validate());
builder.Services.AddValidatedOptions<BackfillOptions>(configuration, BackfillOptions.SectionName, options => options.Validate());

builder.Services.AddSingleton(TimeProvider.System);

// Infrastructure: PostgreSQL
builder.Services.AddOmegaPersistence(configuration);

// Market data: Binance Spot (WebSocket stream + REST history)
builder.Services.AddSingleton<IWebSocketTransportFactory, ClientWebSocketTransportFactory>();
builder.Services.AddSingleton<IMarketDataStream>(services => new BinanceKlineStream(
    Options<MarketDataOptions>(services),
    services.GetRequiredService<IWebSocketTransportFactory>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<BinanceKlineStream>>()));
builder.Services.AddSingleton<IHistoricalCandleSource>(services =>
{
    var options = Options<MarketDataOptions>(services);

    // Long-lived client; rotating pooled connections picks up DNS changes.
    var httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        BaseAddress = new Uri(options.RestBaseUrl.TrimEnd('/') + "/"),
        Timeout = Timeout.InfiniteTimeSpan, // per-request timeout: MarketData:RestRequestTimeout
    };

    return new BinanceRestKlineSource(
        httpClient, options, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<BinanceRestKlineSource>>());
});

// Application
builder.Services.AddSingleton(services => new MarketStateService(
    services.GetRequiredService<ICandleStore>(), Options<MarketStateOptions>(services), services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(services => new CandleGapFiller(
    services.GetRequiredService<IHistoricalCandleSource>(),
    services.GetRequiredService<ICandleStore>(),
    services.GetRequiredService<ISystemEventStore>(),
    Options<BackfillOptions>(services),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<CandleGapFiller>>()));
builder.Services.AddSingleton(services => new MarketDataIngestionService(
    services.GetRequiredService<IMarketDataStream>(),
    services.GetRequiredService<ICandleStore>(),
    services.GetRequiredService<ISystemEventStore>(),
    Options<MarketDataOptions>(services),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<MarketDataIngestionService>>(),
    gapFiller: Options<BackfillOptions>(services).Enabled ? services.GetRequiredService<CandleGapFiller>() : null));
builder.Services.AddSingleton(services => new MarketDataFreshnessMonitor(
    services.GetRequiredService<MarketStateService>(),
    services.GetRequiredService<ISystemEventStore>(),
    Options<MarketStateOptions>(services),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<MarketDataFreshnessMonitor>>()));

builder.Services.AddHostedService<MarketDataIngestionWorker>();
builder.Services.AddHostedService<MarketDataFreshnessWorker>();

using var host = builder.Build();

// The schema must match this build before anything reads or writes data.
if (!await host.Services.EnsureDatabaseReadyAsync(CancellationToken.None))
{
    return 1;
}

await host.RunAsync();
return 0;

static T Options<T>(IServiceProvider services)
    where T : class => services.GetRequiredService<IOptions<T>>().Value;
