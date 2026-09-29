using Omega.Core.Configuration;

namespace Omega.Api.Contracts;

/// <summary>
/// Response of <c>GET /api/system/status</c>.
/// </summary>
/// <param name="Service">Name of the responding service.</param>
/// <param name="TradingMode">Configured trading mode, upper case (for example BACKTEST).</param>
/// <param name="TimestampUtc">Server time in UTC when the response was built.</param>
public sealed record SystemStatusResponse(string Service, string TradingMode, DateTimeOffset TimestampUtc)
{
    public const string ServiceName = "Omega.Api";

    public static SystemStatusResponse Create(TradingOptions tradingOptions, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(tradingOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        return new SystemStatusResponse(
            ServiceName,
            tradingOptions.Mode.ToString().ToUpperInvariant(),
            timeProvider.GetUtcNow());
    }
}
