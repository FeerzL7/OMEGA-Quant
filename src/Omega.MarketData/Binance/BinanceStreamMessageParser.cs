using System.Globalization;
using System.Text.Json;

namespace Omega.MarketData.Binance;

/// <summary>
/// Parses raw-stream messages as documented in the official Binance Spot
/// "WebSocket Streams" specification. Never throws for bad input: malformed
/// messages become <see cref="BinanceInvalidMessage"/>.
/// </summary>
internal static class BinanceStreamMessageParser
{
    private const NumberStyles DecimalStyle = NumberStyles.AllowDecimalPoint;

    public static BinanceStreamMessage Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BinanceInvalidMessage("Empty message.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new BinanceInvalidMessage("Message is not a JSON object.");
            }

            if (!root.TryGetProperty("e", out var eventType))
            {
                return root.TryGetProperty("id", out _)
                    ? new BinanceIgnoredMessage("Control response.")
                    : new BinanceInvalidMessage("Missing event type ('e').");
            }

            return eventType.GetString() switch
            {
                "kline" => ParseKline(root),
                "serverShutdown" => new BinanceServerShutdownMessage(GetInt64(root, "E")),
                var other => new BinanceIgnoredMessage($"Event '{other}' is not consumed."),
            };
        }
        catch (JsonException ex)
        {
            return new BinanceInvalidMessage($"Malformed JSON: {ex.Message}");
        }
        catch (MessageFormatException ex)
        {
            return new BinanceInvalidMessage(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // JsonElement accessors throw these when a value has the wrong JSON type or range.
            return new BinanceInvalidMessage($"Unexpected value: {ex.Message}");
        }
    }

    private static BinanceKlineMessage ParseKline(JsonElement root)
    {
        var k = GetProperty(root, "k");

        var kline = new BinanceKline(
            StartTimeMs: GetInt64(k, "t"),
            CloseTimeMs: GetInt64(k, "T"),
            Symbol: GetString(k, "s"),
            Interval: GetString(k, "i"),
            Open: GetDecimal(k, "o"),
            High: GetDecimal(k, "h"),
            Low: GetDecimal(k, "l"),
            Close: GetDecimal(k, "c"),
            BaseVolume: GetDecimal(k, "v"),
            QuoteVolume: GetDecimal(k, "q"),
            TradeCount: GetInt64(k, "n"),
            IsClosed: GetProperty(k, "x").GetBoolean());

        return new BinanceKlineMessage(GetInt64(root, "E"), kline);
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw new MessageFormatException($"Missing field '{name}'.");

    private static long GetInt64(JsonElement element, string name) => GetProperty(element, name).GetInt64();

    private static string GetString(JsonElement element, string name) =>
        GetProperty(element, name).GetString() is { Length: > 0 } value
            ? value
            : throw new MessageFormatException($"Field '{name}' is empty.");

    private static decimal GetDecimal(JsonElement element, string name)
    {
        var text = GetString(element, name);

        return decimal.TryParse(text, DecimalStyle, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new MessageFormatException($"Field '{name}' is not a valid decimal: '{text}'.");
    }

    private sealed class MessageFormatException(string message) : Exception(message);
}
