using Microsoft.Extensions.Options;
using Omega.Api.Endpoints;
using System.Text.Json.Serialization;
using Omega.Application.Backtesting;
using Omega.Application.Features;
using Omega.Application.MarketData;
using Omega.Core.Configuration;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Features;
using Omega.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.AddValidatedOptions<TradingOptions>(configuration, TradingOptions.SectionName,
    options => Enum.IsDefined(options.Mode) ? [] : ["Trading:Mode is not a valid trading mode."]);
builder.Services.AddValidatedOptions<MarketStateOptions>(configuration, MarketStateOptions.SectionName, options => options.Validate());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOmegaPersistence(configuration);
builder.Services.AddSingleton(services => new MarketStateService(
    services.GetRequiredService<ICandleStore>(),
    services.GetRequiredService<IOptions<MarketStateOptions>>().Value,
    services.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton(new FeatureEngine(FeatureSets.V1()));
builder.Services.AddSingleton(services => new FeatureService(
    services.GetRequiredService<ICandleStore>(), services.GetRequiredService<FeatureEngine>()));

builder.Services.AddSingleton(services => new BacktestService(
    services.GetRequiredService<ICandleStore>(),
    services.GetRequiredService<IBacktestRunStore>(),
    services.GetRequiredService<FeatureEngine>(),
    services.GetRequiredService<TimeProvider>()));

// Enums as names in every response (readable, stable across enum reordering).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

var app = builder.Build();

// The API reads the database: the schema must match this build.
if (!await app.Services.EnsureDatabaseReadyAsync(CancellationToken.None))
{
    return 1;
}

app.UseHttpsRedirection();

// Liveness only: reports that the API process is up.
app.MapHealthChecks("/health");
app.MapSystemEndpoints();
app.MapMarketEndpoints();
app.MapFeatureEndpoints();
app.MapBacktestEndpoints();

await app.RunAsync();
return 0;
