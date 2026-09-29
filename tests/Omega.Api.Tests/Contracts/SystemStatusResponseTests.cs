using Omega.Api.Contracts;
using Omega.Core.Configuration;
using Omega.Core.Trading;

namespace Omega.Api.Tests.Contracts;

public class SystemStatusResponseTests
{
    [Fact]
    public void Reports_configured_mode_in_upper_case_and_utc_time()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var options = new TradingOptions { Mode = TradingMode.Paper };

        var response = SystemStatusResponse.Create(options, new FixedTimeProvider(now));

        Assert.Equal("Omega.Api", response.Service);
        Assert.Equal("PAPER", response.TradingMode);
        Assert.Equal(now, response.TimestampUtc);
        Assert.Equal(TimeSpan.Zero, response.TimestampUtc.Offset);
    }

    [Fact]
    public void Reports_backtest_when_trading_section_is_missing()
    {
        var response = SystemStatusResponse.Create(new TradingOptions(), TimeProvider.System);

        Assert.Equal("BACKTEST", response.TradingMode);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
