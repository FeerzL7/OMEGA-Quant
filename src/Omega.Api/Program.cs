using Omega.Api.Endpoints;
using Omega.Core.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<TradingOptions>()
    .Bind(builder.Configuration.GetSection(TradingOptions.SectionName))
    .Validate(options => Enum.IsDefined(options.Mode), "Trading:Mode is not a valid trading mode.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseHttpsRedirection();

// Liveness only: reports that the API process is up. It does not check
// Binance, PostgreSQL or models, because none of them exist yet (Phase 0).
app.MapHealthChecks("/health");
app.MapSystemEndpoints();

app.Run();
