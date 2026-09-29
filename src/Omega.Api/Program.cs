using Microsoft.Extensions.Options;
using Omega.Api.Endpoints;
using Omega.Application.MarketData;
using Omega.Core.Configuration;
using Omega.Core.MarketData;
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

await app.RunAsync();
return 0;
