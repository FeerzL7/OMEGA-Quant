namespace Omega.Core.Trading;

/// <summary>
/// Exchange rules an order quantity must respect (CLAUDE.md §19). Values must come from the exchange's own
/// metadata (Binance exchangeInfo: LOT_SIZE, NOTIONAL/MIN_NOTIONAL); nothing here assumes them.
/// </summary>
/// <param name="QuantityStep">Quantities are rounded down to a multiple of this, if set.</param>
/// <param name="MinQuantity">Smallest allowed quantity, if set.</param>
/// <param name="MinNotional">Smallest allowed order value in quote currency, if set.</param>
public sealed record InstrumentFilters(decimal? QuantityStep = null, decimal? MinQuantity = null, decimal? MinNotional = null)
{
    public static InstrumentFilters None { get; } = new();
}
