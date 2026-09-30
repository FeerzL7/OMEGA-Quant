using Omega.Application.Features;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Features;
using static Omega.Application.Tests.TestSupport.TestCandles;

namespace Omega.Application.Tests.Features;

public class FeatureServiceTests
{
    private readonly InMemoryCandleStore _candles = new();
    private readonly FeatureService _service;

    public FeatureServiceTests()
    {
        _service = new FeatureService(_candles, new FeatureEngine(FeatureSets.V1()));
    }

    [Fact]
    public async Task Without_candles_nothing_is_computed()
    {
        var result = await _service.ComputeLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(FeatureServiceErrors.NoData, result.Error!.Code);
    }

    [Fact]
    public async Task Short_history_gives_a_warming_up_vector_at_the_latest_candle()
    {
        Seed(0, 30);

        var vector = (await _service.ComputeLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None)).Value;

        Assert.Equal(OpenTime(29), vector.OpenTimeUtc);
        Assert.False(vector.IsComplete);
        Assert.NotNull(vector.Values["log_return_12"]);
        Assert.Null(vector.Values["rsi_14"]);
    }

    [Fact]
    public async Task Enough_contiguous_history_gives_a_complete_vector()
    {
        Seed(0, 300);

        var vector = (await _service.ComputeLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None)).Value;

        Assert.True(vector.IsComplete);
        Assert.Equal(OpenTime(299), vector.OpenTimeUtc);
    }

    [Fact]
    public async Task Gap_in_the_needed_window_blocks_the_computation()
    {
        Seed(0, 200);
        Seed(201, 100);

        var result = await _service.ComputeLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(FeatureErrors.NotContiguous, result.Error!.Code);
    }

    [Fact]
    public async Task Gap_older_than_the_window_does_not_matter()
    {
        Seed(0, 10);
        Seed(20, 300);

        var result = await _service.ComputeLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.True(result.Value.IsComplete);
    }

    private void Seed(int first, int count)
    {
        for (var i = first; i < first + count; i++)
        {
            // Close varies so that every feature is defined.
            _candles.Seed(Candle(i, close: 100m + (i % 7 * 0.125m))); // stays within the candle's 99-101 range
        }
    }
}
