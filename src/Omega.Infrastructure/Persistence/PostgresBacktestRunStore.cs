using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Omega.Backtesting;
using Omega.Core.MarketData;

namespace Omega.Infrastructure.Persistence;

/// <summary><see cref="IBacktestRunStore"/> on PostgreSQL (table <c>backtest_runs</c>).</summary>
public sealed class PostgresBacktestRunStore(NpgsqlDataSource dataSource) : IBacktestRunStore
{
    public const int MaxLimit = 500;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const string SummaryColumns =
        "id, created_at, period_label, strategy_name, strategy_version, symbol, interval_code, trading_start, trading_end, " +
        "dataset_sha256, trade_count, total_return";

    public Task SaveAsync(BacktestRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var result = run.Result;

        return NpgsqlErrors.TranslateAsync("Saving backtest run", async () =>
        {
            await using var command = dataSource.CreateCommand($"""
                INSERT INTO backtest_runs ({SummaryColumns}, feature_set_hash, result)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
                """);
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = run.Id });
            command.Parameters.Add(Timestamp(run.CreatedAtUtc));
            command.Parameters.Add(Text(run.PeriodLabel));
            command.Parameters.Add(Text(result.Strategy.Name));
            command.Parameters.Add(Text(result.Strategy.Version));
            command.Parameters.Add(Text(result.Dataset.Symbol));
            command.Parameters.Add(Text(result.Dataset.Interval.ToCode()));
            command.Parameters.Add(Timestamp(run.TradingStartUtc));
            command.Parameters.Add(Timestamp(run.TradingEndUtc));
            command.Parameters.Add(Text(result.Dataset.Sha256));
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = result.Metrics.TradeCount });
            command.Parameters.Add(new NpgsqlParameter<double> { TypedValue = result.Metrics.TotalReturn });
            command.Parameters.Add(Text(result.FeatureSetHash));
            command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(result, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    public Task<BacktestRun?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Reading backtest run", async () =>
        {
            await using var command = dataSource.CreateCommand(
                "SELECT id, created_at, period_label, trading_start, trading_end, result FROM backtest_runs WHERE id = $1");
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<BacktestResult>(reader.GetString(5), Json)
                ?? throw new InvalidDataException($"Backtest run {id} has an empty result.");

            return new BacktestRun(
                reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), result);
        });

    public Task<IReadOnlyList<BacktestRunSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);

        return NpgsqlErrors.TranslateAsync("Listing backtest runs", async () =>
        {
            await using var command = dataSource.CreateCommand($"SELECT {SummaryColumns} FROM backtest_runs ORDER BY created_at DESC, id LIMIT $1");
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });

            var runs = new List<BacktestRunSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var code = reader.GetString(6);
                runs.Add(new BacktestRunSummary(
                    reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5),
                    CandleIntervalExtensions.TryParseCode(code, out var interval) ? interval : throw new InvalidDataException($"Unknown interval '{code}'."),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetString(9),
                    reader.GetInt32(10), reader.GetDouble(11)));
            }

            IReadOnlyList<BacktestRunSummary> result = runs;
            return result;
        });
    }

    public Task<int> CountEvaluationsAsync(
        string strategyName, string symbol, CandleInterval interval, DateTimeOffset tradingStartUtc, DateTimeOffset tradingEndUtc,
        CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Counting backtest evaluations", async () =>
        {
            await using var command = dataSource.CreateCommand("""
                SELECT count(*) FROM backtest_runs
                WHERE strategy_name = $1 AND symbol = $2 AND interval_code = $3 AND trading_start = $4 AND trading_end = $5
                """);
            command.Parameters.Add(Text(strategyName));
            command.Parameters.Add(Text(symbol));
            command.Parameters.Add(Text(interval.ToCode()));
            command.Parameters.Add(Timestamp(tradingStartUtc));
            command.Parameters.Add(Timestamp(tradingEndUtc));

            return (int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        });

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.TimestampTz };
}
