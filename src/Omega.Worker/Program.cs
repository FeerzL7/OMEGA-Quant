using Omega.Core.Configuration;
using Omega.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<TradingOptions>()
    .Bind(builder.Configuration.GetSection(TradingOptions.SectionName))
    .Validate(options => Enum.IsDefined(options.Mode), "Trading:Mode is not a valid trading mode.")
    .ValidateOnStart();

builder.Services.AddHostedService<OmegaWorker>();

var host = builder.Build();
host.Run();
