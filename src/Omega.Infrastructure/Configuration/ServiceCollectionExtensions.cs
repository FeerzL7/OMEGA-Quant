using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.SystemEvents;
using Omega.Infrastructure.Persistence;
using Omega.Infrastructure.Persistence.Migrations;

namespace Omega.Infrastructure.Configuration;

/// <summary>Shared composition used by every host (API and Worker).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <typeparamref name="TOptions"/> from <paramref name="sectionName"/> and fails
    /// start-up with every message returned by <paramref name="validate"/>.
    /// </summary>
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName,
        Func<TOptions, IReadOnlyList<string>> validate)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(validate);

        services.AddSingleton<IValidateOptions<TOptions>>(new ErrorListValidator<TOptions>(validate));

        return services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateOnStart();
    }

    /// <summary>
    /// PostgreSQL: validated <see cref="DatabaseOptions"/>, one shared <see cref="NpgsqlDataSource"/>,
    /// <see cref="DatabaseMigrator"/> and the stores behind the Core abstractions.
    /// </summary>
    public static IServiceCollection AddOmegaPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName, options => options.Validate());

        services.AddSingleton(provider =>
            new NpgsqlDataSourceBuilder(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
                .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>())
                .Build());
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<ICandleStore, PostgresCandleStore>();
        services.AddSingleton<ISystemEventStore, PostgresSystemEventStore>();
        services.AddSingleton<IBacktestRunStore, PostgresBacktestRunStore>();
        services.AddSingleton<Omega.Execution.Paper.IPaperTradingStore, PostgresPaperTradingStore>();

        return services;
    }

    /// <summary>
    /// Verifies (and, if <see cref="DatabaseOptions.ApplyMigrationsOnStartup"/>, applies) the schema.
    /// Returns false, after logging why, when the host must not start.
    /// </summary>
    public static async Task<bool> EnsureDatabaseReadyAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Omega.Startup");

        try
        {
            var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            await services.GetRequiredService<DatabaseMigrator>()
                .EnsureUpToDateAsync(options.ApplyMigrationsOnStartup, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException or NpgsqlException)
        {
            StartupLog.DatabaseNotReady(logger, ex);
            return false;
        }
    }

    private sealed class ErrorListValidator<TOptions>(Func<TOptions, IReadOnlyList<string>> validate) : IValidateOptions<TOptions>
        where TOptions : class
    {
        public ValidateOptionsResult Validate(string? name, TOptions options)
        {
            var errors = validate(options);
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Critical, Message = "Start-up aborted: the database is not ready.")]
    public static partial void DatabaseNotReady(ILogger logger, Exception exception);
}
