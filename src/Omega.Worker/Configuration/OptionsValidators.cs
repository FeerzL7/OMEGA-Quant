using Microsoft.Extensions.Options;
using Omega.Infrastructure.Persistence;
using Omega.MarketData.Configuration;

namespace Omega.Worker.Configuration;

/// <summary>Fails start-up with every problem found in the MarketData section.</summary>
public sealed class MarketDataOptionsValidator : IValidateOptions<MarketDataOptions>
{
    public ValidateOptionsResult Validate(string? name, MarketDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ToResult(options.Validate());
    }

    internal static ValidateOptionsResult ToResult(IReadOnlyList<string> errors) =>
        errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
}

/// <summary>Fails start-up when the Database section is missing or invalid (without echoing the connection string).</summary>
public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MarketDataOptionsValidator.ToResult(options.Validate());
    }
}
