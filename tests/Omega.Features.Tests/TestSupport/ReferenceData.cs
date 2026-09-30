using System.Globalization;
using System.Reflection;
using Omega.Core.MarketData;

namespace Omega.Features.Tests.TestSupport;

/// <summary>Candles and expected values produced by TestData/generate_reference.py.</summary>
internal static class ReferenceData
{
    public static Candle[] Candles { get; } = LoadCandles();

    /// <summary>Expected value per feature name, per candle index; null where the reference is undefined.</summary>
    public static IReadOnlyList<Dictionary<string, double?>> Expected { get; } = LoadExpected();

    public static Candle Candle(DateTimeOffset open, decimal close, decimal volume = 10m, string symbol = "BTCUSDT") =>
        Omega.Core.MarketData.Candle.Create(
            symbol, CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
            close, close, close, close, volume, volume * close, 10).Value;

    private static Candle[] LoadCandles() => [.. ReadCsv("candles.csv").Select(row =>
    {
        var open = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row["open_time_ms"], CultureInfo.InvariantCulture));
        return Omega.Core.MarketData.Candle.Create(
            "BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
            Dec(row["open"]), Dec(row["high"]), Dec(row["low"]), Dec(row["close"]),
            Dec(row["base_volume"]), Dec(row["quote_volume"]), long.Parse(row["trade_count"], CultureInfo.InvariantCulture)).Value;
    })];

    private static List<Dictionary<string, double?>> LoadExpected() => [.. ReadCsv("expected_features.csv").Select(row =>
        row.Where(pair => pair.Key != "index").ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Length == 0 ? (double?)null : double.Parse(pair.Value, CultureInfo.InvariantCulture),
            StringComparer.Ordinal))];

    private static decimal Dec(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static List<Dictionary<string, string>> ReadCsv(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource {resource} not found.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines[0].Split(',');

        return [.. lines.Skip(1).Select(line =>
        {
            var cells = line.Split(',');
            return header.Select((name, i) => (name, cells[i])).ToDictionary(p => p.name, p => p.Item2, StringComparer.Ordinal);
        })];
    }
}
