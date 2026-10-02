using Microsoft.Extensions.Options;
using Omega.Application.MarketData;
using Omega.Application.Paper;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Risk;
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

// Trading mode guard: only the modes whose execution exists may run. Testnet and live execution do not exist yet
// (phases 14-16), so the worker refuses them instead of silently doing something else.
var mode = configuration.GetSection(TradingOptions.SectionName).Get<TradingOptions>()?.Mode ?? TradingMode.Backtest;
if (mode is TradingMode.Testnet or TradingMode.Live)
{
    Console.Error.WriteLine($"Trading:Mode {mode} is not implemented yet (roadmap phases 14-16). Refusing to start.");
    return 2;
}

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

// Paper trading (Phase 12): only in Paper mode.
if (mode == TradingMode.Paper)
{
    builder.Services.AddValidatedOptions<PaperTradingOptions>(configuration, PaperTradingOptions.SectionName, options => options.Validate());
    var risk = configuration.GetSection(RiskLimits.SectionName).Get<RiskLimits>() ?? RiskLimits.Default;
    var riskErrors = risk.Validate();
    if (riskErrors.Count > 0)
    {
        Console.Error.WriteLine($"Invalid Risk configuration: {string.Join(" ", riskErrors)}");
        return 2;
    }

    builder.Services.AddSingleton(risk);
    builder.Services.AddSingleton(new FeatureEngine(FeatureSets.V1()));
    builder.Services.AddSingleton(new ResearchModelsDirectory(configuration["Research:ModelsDirectory"]));
    builder.Services.AddHostedService<PaperTradingWorker>();
}

using var host = builder.Build();

// The schema must match this build before anything reads or writes data.
if (!await host.Services.EnsureDatabaseReadyAsync(CancellationToken.None))
{
    return 1;
}

await host.RunAsync();

// 0 on a clean shutdown; a component that refused to run (for example paper trading) sets a non-zero code.
return Environment.ExitCode;

static T Options<T>(IServiceProvider services)
    where T : class => services.GetRequiredService<IOptions<T>>().Value;
