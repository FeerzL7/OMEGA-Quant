using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Api.Endpoints;
using Omega.Core.MarketData;
using Omega.Core.SystemEvents;

namespace Omega.Api.Tests.Endpoints;

public class MonitoringEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Candles_are_the_latest_n_oldest_first()
    {
        var store = new Candles(Enumerable.Range(0, 50).Select(Candle).ToList());

        var result = await MonitoringEndpoints.CandlesAsync("BTCUSDT", "5m", store, CancellationToken.None, limit: 10);

        var candles = Assert.IsType<Ok<IReadOnlyList<CandleResponse>>>(result.Result).Value!;
        Assert.Equal(10, candles.Count);
        Assert.Equal(T0.AddMinutes(5 * 40), candles[0].OpenTimeUtc);
        Assert.Equal(T0.AddMinutes(5 * 49), candles[^1].OpenTimeUtc);
    }

    [Fact]
    public async Task No_candles_is_an_empty_list_and_bad_parameters_are_rejected()
    {
        var empty = await MonitoringEndpoints.CandlesAsync("BTCUSDT", "5m", new Candles([]), CancellationToken.None);
        var bad = await MonitoringEndpoints.CandlesAsync("btc", "1h", new Candles([]), CancellationToken.None, limit: 5_000);

        Assert.Empty(Assert.IsType<Ok<IReadOnlyList<CandleResponse>>>(empty.Result).Value!);
        Assert.Equal(["symbol", "interval", "limit"], Assert.IsType<ValidationProblem>(bad.Result).ProblemDetails.Errors.Keys);
    }

    [Fact]
    public async Task Events_are_returned_with_their_severity_as_text()
    {
        var events = new Events([new SystemEvent(T0, "MarketData", "CANDLE_GAP_DETECTED", SystemEventSeverity.Warning, "Gap.")]);

        var result = await MonitoringEndpoints.EventsAsync(events, CancellationToken.None, limit: 5);

        Assert.Equal("Warning", Assert.Single(Assert.IsType<Ok<IReadOnlyList<SystemEventResponse>>>(result.Result).Value!).Severity);
        Assert.IsType<ValidationProblem>((await MonitoringEndpoints.EventsAsync(events, CancellationToken.None, limit: 0)).Result);
    }

    private static Candle Candle(int i)
    {
        var open = T0.AddMinutes(5 * i);
        return Omega.Core.MarketData.Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1), 100m, 101m, 99m, 100m, 1m, 100m, 1).Value;
    }

    private sealed class Candles(List<Candle> candles) : ICandleStore
    {
        public Task<IReadOnlyList<Candle>> GetRangeAsync(string s, CandleInterval i, DateTimeOffset from, DateTimeOffset to, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<Candle>>([.. candles.Where(x => x.OpenTimeUtc >= from && x.OpenTimeUtc < to)]);

        public Task<Candle?> GetEarliestAsync(string s, CandleInterval i, CancellationToken c) => Task.FromResult(candles.FirstOrDefault());

        public Task<Candle?> GetLatestAsync(string s, CandleInterval i, CancellationToken c) => Task.FromResult(candles.LastOrDefault());

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(string s, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) => throw new NotSupportedException();

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset at, CancellationToken c) => throw new NotSupportedException();
    }

    private sealed class Events(IReadOnlyList<SystemEvent> events) : ISystemEventStore
    {
        public Task AppendAsync(SystemEvent systemEvent, CancellationToken c) => Task.CompletedTask;

        public Task<IReadOnlyList<SystemEvent>> GetRecentAsync(int limit, CancellationToken c) => Task.FromResult(events);
    }
}
