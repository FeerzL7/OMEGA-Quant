using Npgsql;
using NpgsqlTypes;
using Omega.Core.MarketData;

namespace Omega.Infrastructure.Persistence;

/// <summary><see cref="ICandleStore"/> on PostgreSQL (table <c>candles</c>).</summary>
public sealed class PostgresCandleStore(NpgsqlDataSource dataSource) : ICandleStore
{
    private const string Columns =
        "symbol, interval_code, open_time, close_time, open_price, high_price, low_price, close_price, " +
        "base_volume, quote_volume, trade_count";

    private const string InsertSql = $"""
        INSERT INTO candles ({Columns}, source, observed_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
        ON CONFLICT ON CONSTRAINT pk_candles DO NOTHING
        """;

    private const string SelectOneSql = $"""
        SELECT {Columns} FROM candles
        WHERE symbol = $1 AND interval_code = $2 AND open_time = $3
        """;

    private const string SelectLatestSql = $"""
        SELECT {Columns} FROM candles
        WHERE symbol = $1 AND interval_code = $2
        ORDER BY open_time DESC
        LIMIT 1
        """;

    private const string SelectRangeSql = $"""
        SELECT {Columns} FROM candles
        WHERE symbol = $1 AND interval_code = $2 AND open_time >= $3 AND open_time < $4
        ORDER BY open_time
        """;

    public Task<CandleSaveOutcome> SaveAsync(
        Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return NpgsqlErrors.TranslateAsync("Saving candle", async () =>
        {
            await using (var insert = dataSource.CreateCommand(InsertSql))
            {
                AddKey(insert, candle.Symbol, candle.Interval, candle.OpenTimeUtc);
                insert.Parameters.Add(Timestamp(candle.CloseTimeUtc));
                insert.Parameters.Add(Numeric(candle.Open));
                insert.Parameters.Add(Numeric(candle.High));
                insert.Parameters.Add(Numeric(candle.Low));
                insert.Parameters.Add(Numeric(candle.Close));
                insert.Parameters.Add(Numeric(candle.BaseVolume));
                insert.Parameters.Add(Numeric(candle.QuoteVolume));
                insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = candle.TradeCount });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = source });
                insert.Parameters.Add(Timestamp(observedAtUtc.ToUniversalTime()));

                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    return CandleSaveOutcome.Inserted;
                }
            }

            // Same key already stored: identical data is a harmless repeat, anything else is a conflict.
            await using var select = dataSource.CreateCommand(SelectOneSql);
            AddKey(select, candle.Symbol, candle.Interval, candle.OpenTimeUtc);
            var stored = (await ReadCandlesAsync(select, cancellationToken).ConfigureAwait(false)).Single();

            return stored == candle ? CandleSaveOutcome.AlreadyStored : CandleSaveOutcome.Conflict;
        });
    }

    public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        return NpgsqlErrors.TranslateAsync("Reading latest candle", async () =>
        {
            await using var command = dataSource.CreateCommand(SelectLatestSql);
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = symbol });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = interval.ToCode() });

            return (await ReadCandlesAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        });
    }

    public Task<IReadOnlyList<Candle>> GetRangeAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset fromOpenTimeUtc,
        DateTimeOffset toOpenTimeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (toOpenTimeUtc <= fromOpenTimeUtc)
        {
            throw new ArgumentException("The end of the range must be after its start.", nameof(toOpenTimeUtc));
        }

        return NpgsqlErrors.TranslateAsync("Reading candle range", async () =>
        {
            await using var command = dataSource.CreateCommand(SelectRangeSql);
            AddKey(command, symbol, interval, fromOpenTimeUtc);
            command.Parameters.Add(Timestamp(toOpenTimeUtc));

            IReadOnlyList<Candle> candles = await ReadCandlesAsync(command, cancellationToken).ConfigureAwait(false);
            return candles;
        });
    }

    private static void AddKey(NpgsqlCommand command, string symbol, CandleInterval interval, DateTimeOffset openTimeUtc)
    {
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = symbol });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = interval.ToCode() });
        command.Parameters.Add(Timestamp(openTimeUtc));
    }

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private static NpgsqlParameter<decimal> Numeric(decimal value) =>
        new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static async Task<List<Candle>> ReadCandlesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var candles = new List<Candle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var intervalCode = reader.GetString(1);
            if (!CandleIntervalExtensions.TryParseCode(intervalCode, out var interval))
            {
                throw new InvalidDataException($"Stored candle has unknown interval code '{intervalCode}'.");
            }

            var result = Candle.Create(
                symbol: reader.GetString(0),
                interval: interval,
                openTimeUtc: reader.GetFieldValue<DateTimeOffset>(2),
                closeTimeUtc: reader.GetFieldValue<DateTimeOffset>(3),
                open: reader.GetDecimal(4),
                high: reader.GetDecimal(5),
                low: reader.GetDecimal(6),
                close: reader.GetDecimal(7),
                baseVolume: reader.GetDecimal(8),
                quoteVolume: reader.GetDecimal(9),
                tradeCount: reader.GetInt64(10));

            // The schema's constraints mirror Candle.Create, so this only fails if they were bypassed.
            candles.Add(result.IsSuccess
                ? result.Value
                : throw new InvalidDataException($"Stored candle violates domain invariants: {result.Error!.Code}."));
        }

        return candles;
    }
}
