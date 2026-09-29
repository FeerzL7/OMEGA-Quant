using Microsoft.Extensions.Options;
using Omega.Core.Configuration;
using Omega.MarketData;
using Omega.MarketData.Binance;
using Omega.MarketData.Configuration;
using Omega.MarketData.Transport;
using Omega.Worker;
using Omega.Worker.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<TradingOptions>()
    .Bind(builder.Configuration.GetSection(TradingOptions.SectionName))
    .Validate(options => Enum.IsDefined(options.Mode), "Trading:Mode is not a valid trading mode.")
    .ValidateOnStart();

builder.Services.AddOptions<MarketDataOptions>()
    .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MarketDataOptions>, MarketDataOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IWebSocketTransportFactory, ClientWebSocketTransportFactory>();
builder.Services.AddSingleton<IMarketDataStream>(services => new BinanceKlineStream(
    services.GetRequiredService<IOptions<MarketDataOptions>>().Value,
    services.GetRequiredService<IWebSocketTransportFactory>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<BinanceKlineStream>>()));

builder.Services.AddHostedService<MarketDataWorker>();

var host = builder.Build();
host.Run();
