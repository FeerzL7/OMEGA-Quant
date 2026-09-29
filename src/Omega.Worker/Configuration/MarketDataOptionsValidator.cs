using Microsoft.Extensions.Options;
using Omega.MarketData.Configuration;

namespace Omega.Worker.Configuration;

/// <summary>Fails start-up with every problem found in the MarketData section.</summary>
public sealed class MarketDataOptionsValidator : IValidateOptions<MarketDataOptions>
{
    public ValidateOptionsResult Validate(string? name, MarketDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
