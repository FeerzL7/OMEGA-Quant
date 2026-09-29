using Microsoft.Extensions.Options;
using Npgsql;
using Omega.Application.MarketData;
using Omega.Core.Configuration;
using Omega.Core.MarketData;
using Omega.Core.SystemEvents;
using Omega.Infrastructure.Persistence;
using Omega.Infrastructure.Persistence.Migrations;
using Omega.MarketData;
using Omega.MarketData.Binance;
using Omega.MarketData.Configuration;
using Omega.MarketData.Transport;
using Omega.Worker;
using Omega.Worker.Configuration;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Services.AddOptions<TradingOptions>()
    .Bind(builder.Configuration.GetSection(TradingOptions.SectionName))
    .Validate(options => Enum.IsDefined(options.Mode), "Trading:Mode is not a valid trading mode.")
    .ValidateOnStart();

builder.Services.AddOptions<MarketDataOptions>()
    .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MarketDataOptions>, MarketDataOptionsValidator>();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);

// Infrastructure: PostgreSQL
builder.Services.AddSingleton(services =>
    new NpgsqlDataSourceBuilder(services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
        .UseLoggerFactory(services.GetRequiredService<ILoggerFactory>())
        .Build());
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddSingleton<ICandleStore, PostgresCandleStore>();
builder.Services.AddSingleton<ISystemEventStore, PostgresSystemEventStore>();

// Market data: Binance Spot
builder.Services.AddSingleton<IWebSocketTransportFactory, ClientWebSocketTransportFactory>();
builder.Services.AddSingleton<IMarketDataStream>(services => new BinanceKlineStream(
    services.GetRequiredService<IOptions<MarketDataOptions>>().Value,
    services.GetRequiredService<IWebSocketTransportFactory>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<BinanceKlineStream>>()));

// Application
builder.Services.AddSingleton(services => new MarketDataIngestionService(
    services.GetRequiredService<IMarketDataStream>(),
    services.GetRequiredService<ICandleStore>(),
    services.GetRequiredService<ISystemEventStore>(),
    services.GetRequiredService<IOptions<MarketDataOptions>>().Value,
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<MarketDataIngestionService>>()));
builder.Services.AddHostedService<MarketDataIngestionWorker>();

using var host = builder.Build();

// The schema must match this build before anything reads or writes data.
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Omega.Worker.Startup");
try
{
    var database = host.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    await host.Services.GetRequiredService<DatabaseMigrator>()
        .EnsureUpToDateAsync(database.ApplyMigrationsOnStartup, CancellationToken.None);
}
catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException or NpgsqlException)
{
    StartupLog.DatabaseNotReady(startupLogger, ex);
    return 1;
}

await host.RunAsync();
return 0;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Critical, Message = "Start-up aborted: the database is not ready.")]
    public static partial void DatabaseNotReady(ILogger logger, Exception exception);
}
