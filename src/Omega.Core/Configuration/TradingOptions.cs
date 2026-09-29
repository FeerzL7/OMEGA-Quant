using Omega.Core.Trading;

namespace Omega.Core.Configuration;

/// <summary>
/// Options bound from the <c>Trading</c> configuration section.
/// Plain object with no framework dependency, so Omega.Core stays independent.
/// </summary>
public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    /// <summary>
    /// Operating mode. Defaults to <see cref="TradingMode.Backtest"/> when the
    /// section or the key is missing.
    /// </summary>
    public TradingMode Mode { get; set; } = TradingMode.Backtest;
}
