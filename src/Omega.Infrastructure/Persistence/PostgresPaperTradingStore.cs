using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Omega.Core.Trading;
using Omega.Execution;
using Omega.Execution.Paper;

namespace Omega.Infrastructure.Persistence;

/// <summary><see cref="IPaperTradingStore"/> on PostgreSQL (migration 0003).</summary>
public sealed class PostgresPaperTradingStore(NpgsqlDataSource dataSource) : IPaperTradingStore
{
    private const string OrderColumns =
        "id, client_order_id, side, type, quantity, stop_price, limit_price, parent_order_id, oco_group, status, filled_quantity, " +
        "average_fill_price, fee, reason, created_at, updated_at";

    private const string SessionColumns = "id, name, symbol, interval_code, config::text, state::text, created_at, updated_at, last_candle_open_time";

    public Task<PaperSessionRecord?> GetSessionAsync(string name, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Reading paper session", async () =>
        {
            await using var command = dataSource.CreateCommand($"SELECT {SessionColumns} FROM paper_sessions WHERE name = $1");
            command.Parameters.Add(Text(name));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Session(reader) : null;
        });

    public Task<IReadOnlyList<PaperSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Listing paper sessions", async () =>
        {
            await using var command = dataSource.CreateCommand($"SELECT {SessionColumns} FROM paper_sessions ORDER BY created_at");
            var sessions = new List<PaperSessionRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(Session(reader));
            }

            IReadOnlyList<PaperSessionRecord> result = sessions;
            return result;
        });

    public Task CreateSessionAsync(PaperSessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return NpgsqlErrors.TranslateAsync("Creating paper session", async () =>
        {
            await using var command = dataSource.CreateCommand(
                "INSERT INTO paper_sessions (id, name, symbol, interval_code, config, state, created_at, updated_at, last_candle_open_time) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)");
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = session.Id });
            command.Parameters.Add(Text(session.Name));
            command.Parameters.Add(Text(session.Symbol));
            command.Parameters.Add(Text(session.Interval));
            command.Parameters.Add(Jsonb(session.ConfigJson));
            command.Parameters.Add(Jsonb(session.StateJson));
            command.Parameters.Add(Time(session.CreatedAtUtc));
            command.Parameters.Add(Time(session.UpdatedAtUtc));
            command.Parameters.Add(NullableTime(session.LastCandleOpenTimeUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    public Task SaveStepAsync(PaperStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        return NpgsqlErrors.TranslateAsync("Saving paper trading step", async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var order in step.Orders)
            {
                await UpsertOrderAsync(connection, transaction, step.SessionId, order, cancellationToken).ConfigureAwait(false);
            }

            foreach (var trade in step.Trades)
            {
                await InsertTradeAsync(connection, transaction, step.SessionId, trade, cancellationToken).ConfigureAwait(false);
            }

            await InsertDecisionAsync(connection, transaction, step.SessionId, step.Decision, cancellationToken).ConfigureAwait(false);

            await using (var update = new NpgsqlCommand(
                "UPDATE paper_sessions SET state = $2, last_candle_open_time = $3, updated_at = now() WHERE id = $1", connection, transaction))
            {
                update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = step.SessionId });
                update.Parameters.Add(Jsonb(step.StateJson));
                update.Parameters.Add(Time(step.CandleOpenTimeUtc));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        });
    }

    public Task<IReadOnlyList<Order>> GetWorkingOrdersAsync(Guid sessionId, CancellationToken cancellationToken) =>
        QueryOrdersAsync(
            $"SELECT {OrderColumns}, (SELECT count(*) FROM paper_order_events e WHERE e.order_id = o.id) FROM paper_orders o " +
            "WHERE session_id = $1 AND status IN ('ACKNOWLEDGED', 'PARTIALLY_FILLED') ORDER BY created_at, client_order_id",
            sessionId, null, cancellationToken);

    public Task<IReadOnlyList<Order>> GetOrdersAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        QueryOrdersAsync(
            $"SELECT {OrderColumns}, (SELECT count(*) FROM paper_order_events e WHERE e.order_id = o.id) FROM paper_orders o " +
            "WHERE session_id = $1 ORDER BY created_at DESC, client_order_id DESC LIMIT $2",
            sessionId, limit, cancellationToken);

    public Task<IReadOnlyList<OrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Reading order events", async () =>
        {
            await using var command = dataSource.CreateCommand("SELECT sequence, status, at, detail FROM paper_order_events WHERE order_id = $1 ORDER BY sequence");
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
            var events = new List<OrderEvent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(new OrderEvent(orderId, reader.GetInt32(0), Codes.Parse<OrderStatus>(reader.GetString(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            IReadOnlyList<OrderEvent> result = events;
            return result;
        });

    public Task<IReadOnlyList<PaperTrade>> GetTradesAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Reading paper trades", async () =>
        {
            await using var command = dataSource.CreateCommand(
                "SELECT entry_candle_open_time, entry_price, quantity, entry_fee, stop_loss, take_profit, exit_candle_open_time, exit_price, exit_fee, " +
                "exit_reason, holding_candles, net_pnl, entry_explanation FROM paper_trades WHERE session_id = $1 ORDER BY id DESC LIMIT $2");
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
            var trades = new List<PaperTrade>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                trades.Add(new PaperTrade(
                    reader.GetFieldValue<DateTimeOffset>(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4),
                    reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetDecimal(7), reader.GetDecimal(8),
                    Codes.Parse<ExitReason>(reader.GetString(9)), reader.GetInt32(10), reader.GetDecimal(11), reader.IsDBNull(12) ? null : reader.GetString(12)));
            }

            IReadOnlyList<PaperTrade> result = trades;
            return result;
        });

    public Task<IReadOnlyList<PaperDecision>> GetDecisionsAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Reading paper decisions", async () =>
        {
            await using var command = dataSource.CreateCommand(
                "SELECT candle_open_time, direction, no_trade_reason, explanation, metrics::text, action, risk_code, risk_detail, equity, notes::text " +
                "FROM paper_decisions WHERE session_id = $1 ORDER BY candle_open_time DESC LIMIT $2");
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
            var decisions = new List<PaperDecision>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                decisions.Add(new PaperDecision(
                    reader.GetFieldValue<DateTimeOffset>(0),
                    Codes.Parse<SignalDirection>(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : Codes.Parse<NoTradeReason>(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(4))!,
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetDecimal(8),
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(9))!));
            }

            IReadOnlyList<PaperDecision> result = decisions;
            return result;
        });

    public Task<long> EnqueueCommandAsync(Guid sessionId, PaperCommandType type, string reason, string requestedBy, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Enqueuing paper command", async () =>
        {
            await using var command = dataSource.CreateCommand(
                "INSERT INTO paper_commands (session_id, command, reason, requested_by, requested_at) VALUES ($1, $2, $3, $4, $5) RETURNING id");
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
            command.Parameters.Add(Text(Codes.Of(type)));
            command.Parameters.Add(Text(reason));
            command.Parameters.Add(Text(requestedBy));
            command.Parameters.Add(Time(requestedAtUtc));
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        });

    public Task<IReadOnlyList<PaperCommand>> GetPendingCommandsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        QueryCommandsAsync("WHERE session_id = $1 AND applied_at IS NULL ORDER BY id", sessionId, null, cancellationToken);

    public Task<IReadOnlyList<PaperCommand>> GetCommandsAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        QueryCommandsAsync("WHERE session_id = $1 ORDER BY id DESC LIMIT $2", sessionId, limit, cancellationToken);

    public Task CompleteCommandAsync(long commandId, Guid sessionId, string result, string stateJson, DateTimeOffset appliedAtUtc, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Completing paper command", async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var complete = new NpgsqlCommand(
                "UPDATE paper_commands SET applied_at = $2, result = $3 WHERE id = $1 AND applied_at IS NULL", connection, transaction))
            {
                complete.Parameters.Add(new NpgsqlParameter<long> { TypedValue = commandId });
                complete.Parameters.Add(Time(appliedAtUtc));
                complete.Parameters.Add(Text(result));
                if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException($"Paper command {commandId} was already applied.");
                }
            }

            await using (var state = new NpgsqlCommand("UPDATE paper_sessions SET state = $2, updated_at = now() WHERE id = $1", connection, transaction))
            {
                state.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
                state.Parameters.Add(Jsonb(stateJson));
                await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        });

    private static async Task UpsertOrderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, Order order, CancellationToken cancellationToken)
    {
        await using (var upsert = new NpgsqlCommand(
            $"INSERT INTO paper_orders (session_id, {OrderColumns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17) " +
            "ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status, filled_quantity = EXCLUDED.filled_quantity, " +
            "average_fill_price = EXCLUDED.average_fill_price, fee = EXCLUDED.fee, reason = EXCLUDED.reason, updated_at = EXCLUDED.updated_at",
            connection, transaction))
        {
            upsert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
            upsert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = order.Id });
            upsert.Parameters.Add(Text(order.ClientOrderId));
            upsert.Parameters.Add(Text(Codes.Of(order.Side)));
            upsert.Parameters.Add(Text(Codes.Of(order.Type)));
            upsert.Parameters.Add(Number(order.Quantity));
            upsert.Parameters.Add(NullableNumber(order.StopPrice));
            upsert.Parameters.Add(NullableNumber(order.LimitPrice));
            upsert.Parameters.Add(NullableGuid(order.ParentOrderId));
            upsert.Parameters.Add(NullableGuid(order.OcoGroup));
            upsert.Parameters.Add(Text(Codes.Of(order.Status)));
            upsert.Parameters.Add(Number(order.FilledQuantity));
            upsert.Parameters.Add(NullableNumber(order.AverageFillPrice));
            upsert.Parameters.Add(Number(order.Fee));
            upsert.Parameters.Add(new NpgsqlParameter { Value = (object?)order.Reason ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            upsert.Parameters.Add(Time(order.CreatedAtUtc));
            upsert.Parameters.Add(Time(order.UpdatedAtUtc));
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var e in order.Events)
        {
            // Sequence numbers continue across restarts; re-saving the same event is a no-op.
            await using var insert = new NpgsqlCommand(
                "INSERT INTO paper_order_events (order_id, sequence, status, at, detail) VALUES ($1, $2, $3, $4, $5) ON CONFLICT (order_id, sequence) DO NOTHING",
                connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = order.Id });
            insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = e.Sequence });
            insert.Parameters.Add(Text(Codes.Of(e.Status)));
            insert.Parameters.Add(Time(e.TimeUtc));
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)e.Detail ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertTradeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, PaperTrade trade, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO paper_trades (session_id, entry_candle_open_time, entry_price, quantity, entry_fee, stop_loss, take_profit, exit_candle_open_time, " +
            "exit_price, exit_fee, exit_reason, holding_candles, net_pnl, entry_explanation) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)",
            connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
        insert.Parameters.Add(Time(trade.EntryCandleOpenTimeUtc));
        insert.Parameters.Add(Number(trade.EntryPrice));
        insert.Parameters.Add(Number(trade.Quantity));
        insert.Parameters.Add(Number(trade.EntryFee));
        insert.Parameters.Add(Number(trade.StopLossPrice));
        insert.Parameters.Add(NullableNumber(trade.TakeProfitPrice));
        insert.Parameters.Add(Time(trade.ExitCandleOpenTimeUtc));
        insert.Parameters.Add(Number(trade.ExitPrice));
        insert.Parameters.Add(Number(trade.ExitFee));
        insert.Parameters.Add(Text(Codes.Of(trade.ExitReason)));
        insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = trade.HoldingCandles });
        insert.Parameters.Add(Number(trade.NetPnl));
        insert.Parameters.Add(new NpgsqlParameter { Value = (object?)trade.EntryExplanation ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertDecisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, PaperDecision decision, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO paper_decisions (session_id, candle_open_time, direction, no_trade_reason, explanation, metrics, action, risk_code, risk_detail, equity, notes) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)",
            connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
        insert.Parameters.Add(Time(decision.CandleOpenTimeUtc));
        insert.Parameters.Add(Text(Codes.Of(decision.Direction)));
        insert.Parameters.Add(new NpgsqlParameter { Value = decision.NoTradeReason is { } r ? Codes.Of(r) : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new NpgsqlParameter { Value = (object?)decision.Explanation ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(Jsonb(JsonSerializer.Serialize(decision.Metrics)));
        insert.Parameters.Add(Text(decision.Action));
        insert.Parameters.Add(new NpgsqlParameter { Value = (object?)decision.RiskCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new NpgsqlParameter { Value = (object?)decision.RiskDetail ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(Number(decision.Equity));
        insert.Parameters.Add(Jsonb(JsonSerializer.Serialize(decision.Notes)));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<Order>> QueryOrdersAsync(string sql, Guid sessionId, int? limit, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Reading paper orders", async () =>
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
            if (limit is { } l) command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = l });

            var orders = new List<Order>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                orders.Add(Order.Restore(
                    reader.GetGuid(0), reader.GetString(1), Codes.Parse<OrderSide>(reader.GetString(2)), Codes.Parse<OrderType>(reader.GetString(3)),
                    reader.GetDecimal(4), reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    reader.GetFieldValue<DateTimeOffset>(14), Codes.Parse<OrderStatus>(reader.GetString(9)), reader.GetDecimal(10),
                    reader.IsDBNull(11) ? null : reader.GetDecimal(11), reader.GetDecimal(12), reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.GetFieldValue<DateTimeOffset>(15), (int)reader.GetInt64(16)));
            }

            IReadOnlyList<Order> result = orders;
            return result;
        });

    private Task<IReadOnlyList<PaperCommand>> QueryCommandsAsync(string where, Guid sessionId, int? limit, CancellationToken cancellationToken) =>
        NpgsqlErrors.TranslateAsync("Reading paper commands", async () =>
        {
            await using var command = dataSource.CreateCommand(
                $"SELECT id, session_id, command, reason, requested_by, requested_at, applied_at, result FROM paper_commands {where}");
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
            if (limit is { } l) command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = l });

            var commands = new List<PaperCommand>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                commands.Add(new PaperCommand(
                    reader.GetInt64(0), reader.GetGuid(1), Codes.Parse<PaperCommandType>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            IReadOnlyList<PaperCommand> result = commands;
            return result;
        });

    private static PaperSessionRecord Session(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter Jsonb(string json) => new() { Value = json, NpgsqlDbType = NpgsqlDbType.Jsonb };

    private static NpgsqlParameter<decimal> Number(decimal value) => new() { TypedValue = value };

    private static NpgsqlParameter NullableNumber(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static NpgsqlParameter NullableGuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private static NpgsqlParameter<DateTimeOffset> Time(DateTimeOffset value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private static NpgsqlParameter NullableTime(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };
}

/// <summary>Enum values as stable upper-snake codes (PARTIALLY_FILLED), the form stored and constrained in the database.</summary>
internal static class Codes
{
    public static string Of<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static T Parse<T>(string code) where T : struct, Enum =>
        Enum.GetValues<T>().FirstOrDefault(v => Of(v) == code) is var value && Of(value) == code
            ? value
            : throw new InvalidDataException($"Unknown {typeof(T).Name} code '{code}'.");
}
