using System.Runtime.CompilerServices;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Core.SystemEvents;
using Omega.MarketData;

namespace Omega.Application.Tests.TestSupport;

/// <summary>Replays a fixed list of events, then ends.</summary>
internal sealed class FakeMarketDataStream(params MarketDataEvent[] events) : IMarketDataStream
{
    public bool WasRead { get; private set; }

    public DateTimeOffset? ReceivedLastKnownOpenTimeUtc { get; private set; }

    public async IAsyncEnumerable<MarketDataEvent> ReadEventsAsync(
        DateTimeOffset? lastKnownOpenTimeUtc, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        WasRead = true;
        ReceivedLastKnownOpenTimeUtc = lastKnownOpenTimeUtc;

        foreach (var marketDataEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return marketDataEvent;
        }
    }
}

/// <summary>Like the real stream: after cancellation it reports a final disconnection, then ends.</summary>
internal sealed class StreamReportingStopOnCancellation(params MarketDataEvent[] events) : IMarketDataStream
{
    public async IAsyncEnumerable<MarketDataEvent> ReadEventsAsync(
        DateTimeOffset? lastKnownOpenTimeUtc, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var marketDataEvent in events)
        {
            yield return marketDataEvent;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Swallowed on purpose, like BinanceKlineStream.
        }

        yield return new ConnectionStatusChangedEvent(ConnectionStatus.Disconnected, "Stream stopped.", DateTimeOffset.UtcNow);
    }
}

/// <summary>Honours cancellation like a real database client.</summary>
internal sealed class CancellationAwareSystemEventStore : ISystemEventStore
{
    public List<SystemEvent> Events { get; } = [];

    public Task AppendAsync(SystemEvent systemEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add(systemEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SystemEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SystemEvent>>([.. Events.AsEnumerable().Reverse().Take(limit)]);
}

internal sealed class InMemoryCandleStore : ICandleStore
{
    private readonly Dictionary<DateTimeOffset, (Candle Candle, string Source, DateTimeOffset ObservedAtUtc)> _rows = [];

    /// <summary>Exceptions thrown by the next SaveAsync calls, in order.</summary>
    public Queue<Exception> SaveFailures { get; } = new();

    /// <summary>When set, every SaveAsync call throws it.</summary>
    public Exception? AlwaysFailWith { get; set; }

    public int SaveCalls { get; private set; }

    public IReadOnlyList<Candle> Candles => [.. _rows.Values.Select(row => row.Candle).OrderBy(c => c.OpenTimeUtc)];

    public (string Source, DateTimeOffset ObservedAtUtc) Lineage(DateTimeOffset openTimeUtc) =>
        (_rows[openTimeUtc].Source, _rows[openTimeUtc].ObservedAtUtc);

    public void Seed(Candle candle) => _rows[candle.OpenTimeUtc] = (candle, "seed", candle.CloseTimeUtc);

    public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        SaveCalls++;

        if (AlwaysFailWith is not null)
        {
            throw AlwaysFailWith;
        }

        if (SaveFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        if (_rows.TryGetValue(candle.OpenTimeUtc, out var existing))
        {
            return Task.FromResult(existing.Candle == candle ? CandleSaveOutcome.AlreadyStored : CandleSaveOutcome.Conflict);
        }

        _rows[candle.OpenTimeUtc] = (candle, source, observedAtUtc);
        return Task.FromResult(CandleSaveOutcome.Inserted);
    }

    public Task<Candle?> GetEarliestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) =>
        Task.FromResult(Candles.FirstOrDefault(c => c.Symbol == symbol && c.Interval == interval));

    public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) =>
        Task.FromResult(Candles.LastOrDefault(c => c.Symbol == symbol && c.Interval == interval));

    public Task<IReadOnlyList<CandleGap>> FindGapsAsync(
        string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken)
    {
        FindGapsCalls++;
        var length = interval.ToTimeSpan();
        var opens = Candles.Where(c => c.Symbol == symbol && c.Interval == interval).Select(c => c.OpenTimeUtc).ToList();
        var gaps = new List<CandleGap>();

        for (var i = 1; i < opens.Count; i++)
        {
            var missing = (int)((opens[i] - opens[i - 1]).Ticks / length.Ticks) - 1;
            var gap = new CandleGap(opens[i - 1] + length, missing);
            if (missing > 0 && gap.FirstMissingOpenTimeUtc < toOpenTimeUtc && opens[i] > fromOpenTimeUtc)
            {
                gaps.Add(gap);
            }
        }

        return Task.FromResult<IReadOnlyList<CandleGap>>(gaps);
    }

    public int FindGapsCalls { get; private set; }

    public Task<IReadOnlyList<Candle>> GetRangeAsync(
        string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken)
    {
        IReadOnlyList<Candle> range = [.. Candles.Where(c => c.OpenTimeUtc >= fromOpenTimeUtc && c.OpenTimeUtc < toOpenTimeUtc)];
        return Task.FromResult(range);
    }
}

internal sealed class InMemorySystemEventStore : ISystemEventStore
{
    public List<SystemEvent> Events { get; } = [];

    public bool Fail { get; set; }

    public IReadOnlyList<string> EventTypes => [.. Events.Select(e => e.EventType)];

    public Task AppendAsync(SystemEvent systemEvent, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new PersistenceException("Database unavailable.", isTransient: true);
        }

        Events.Add(systemEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SystemEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<SystemEvent> recent = [.. Events.AsEnumerable().Reverse().Take(limit)];
        return Task.FromResult(recent);
    }
}

internal static class TestCandles
{
    public static readonly DateTimeOffset FirstOpenTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset OpenTime(int index) => FirstOpenTime.AddMinutes(5 * index);

    public static Candle Candle(int index, decimal close = 100.5m)
    {
        var open = OpenTime(index);

        return Omega.Core.MarketData.Candle.Create(
            "BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
            100m, 101m, 99m, close, 12.5m + (index % 5), 1250m, 42).Value;
    }

    public static CandleClosedEvent Closed(int index, decimal close = 100.5m) =>
        new(Candle(index, close), "test-source", OpenTime(index).AddMinutes(5).AddMilliseconds(150));
}

/// <summary>Historical source over a fixed set of available candle indices; failures can be scripted.</summary>
internal sealed class FakeHistoricalSource(params int[] availableIndices) : IHistoricalCandleSource
{
    public Queue<Exception> Failures { get; } = new();

    public Exception? AlwaysFailWith { get; set; }

    public int Calls { get; private set; }

    public string SourceName => "test-history";

    public Task<IReadOnlyList<Candle>> GetClosedCandlesAsync(
        string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken)
    {
        Calls++;

        if (AlwaysFailWith is not null)
        {
            throw AlwaysFailWith;
        }

        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        IReadOnlyList<Candle> candles = [.. availableIndices
            .Select(index => TestCandles.Candle(index))
            .Where(c => c.OpenTimeUtc >= fromOpenTimeUtc && c.OpenTimeUtc < toOpenTimeUtc)
            .OrderBy(c => c.OpenTimeUtc)];
        return Task.FromResult(candles);
    }
}

/// <summary>Clock set by the test; timers still use real time.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
