using Omega.Core.Trading;

namespace Omega.Strategy.Models;

/// <summary>
/// Mean barrier outcomes in ATR14 multiples, estimated on the development period
/// (<c>outcome_profile.json</c>, format <c>omega-outcome-profile-v1</c>).
/// </summary>
/// <param name="WinMean">Mean outcome of TP_FIRST samples (≈ +target ATR multiple).</param>
/// <param name="LossMean">Mean outcome of SL_FIRST and TIMEOUT samples (negative).</param>
public sealed record OutcomeProfile(double WinMean, double LossMean);

/// <summary>Expected value of one long and its parts, as returns on the position value.</summary>
public sealed record ExpectedValueBreakdown(double Probability, double Gain, double Loss, double Costs, double ExpectedReturn);

/// <summary>
/// EV = P(win)·Gain − P(loss)·Loss − Costs (CLAUDE.md §7), with P the calibrated probability. Same formula as the
/// Python reference (omega_ml.expected_value); a parity test checks it.
/// </summary>
public static class ExpectedValueCalculator
{
    /// <param name="probability">Calibrated P(TP_FIRST).</param>
    /// <param name="atr">ATR14 at the decision candle.</param>
    /// <param name="price">Reference price (decision close).</param>
    /// <param name="profile">Outcome magnitudes in ATR multiples.</param>
    /// <param name="costs">Execution costs; the target exit is a limit order, the others are market orders.</param>
    public static ExpectedValueBreakdown Compute(double probability, double atr, double price, OutcomeProfile profile, TradingCosts costs)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentOutOfRangeException.ThrowIfLessThan(probability, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(probability, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(atr, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(price, 0);

        var a = atr / price;
        var gain = profile.WinMean * a;
        var loss = profile.LossMean * a;
        var totalCosts = costs.MarketOrder + (probability * costs.LimitOrder) + ((1 - probability) * costs.MarketOrder);

        return new ExpectedValueBreakdown(
            probability, gain, loss, totalCosts, (probability * gain) + ((1 - probability) * loss) - totalCosts);
    }
}
