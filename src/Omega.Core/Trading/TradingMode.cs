namespace Omega.Core.Trading;

/// <summary>
/// Operating mode of the OMEGA pipeline.
/// </summary>
/// <remarks>
/// <see cref="Backtest"/> is deliberately the zero value: an unset or
/// default-initialized mode must never resolve to a mode that reaches an exchange.
/// Enabling <see cref="Live"/> will require explicit configuration and dedicated
/// safeguards (roadmap phases 14-16); none exist in Phase 0.
/// </remarks>
public enum TradingMode
{
    Backtest = 0,
    Paper = 1,
    Testnet = 2,
    Live = 3,
}
