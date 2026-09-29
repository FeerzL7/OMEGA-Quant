using System.Globalization;
using System.Text.Json;

namespace Omega.MarketData.Binance;

/// <summary>One row of a <c>GET /api/v3/klines</c> response, as documented by Binance Spot.</summary>
internal sealed record BinanceRestKlineRow(
    long OpenTimeMs,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal BaseVolume,
    long CloseTimeMs,
    decimal QuoteVolume,
    long TradeCount)
{
    /// <summary>The REST payload has no closed flag: the caller decides from the close time.</summary>
    public BinanceKline ToKline(string symbol, string intervalCode, bool isClosed) => new(
        OpenTimeMs, CloseTimeMs, symbol, intervalCode, Open, High, Low, Close, BaseVolume, QuoteVolume, TradeCount, isClosed);
}

internal static class BinanceRestKlineParser
{
    /// <summary>Parses the response body. Throws <see cref="FormatException"/> for anything unexpected.</summary>
    public static IReadOnlyList<BinanceRestKlineRow> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Response is not a JSON array.");
            }

            var rows = new List<BinanceRestKlineRow>();

            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 9)
                {
                    throw new FormatException("Kline row is not an array with at least 9 elements.");
                }

                rows.Add(new BinanceRestKlineRow(
                    OpenTimeMs: row[0].GetInt64(),
                    Open: Decimal(row[1]),
                    High: Decimal(row[2]),
                    Low: Decimal(row[3]),
                    Close: Decimal(row[4]),
                    BaseVolume: Decimal(row[5]),
                    CloseTimeMs: row[6].GetInt64(),
                    QuoteVolume: Decimal(row[7]),
                    TradeCount: row[8].GetInt64()));
            }

            return rows;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Malformed JSON: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException($"Unexpected value type: {ex.Message}", ex);
        }
    }

    private static decimal Decimal(JsonElement element) =>
        element.GetString() is { Length: > 0 } text
        && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"'{element}' is not a valid decimal string.");
}
