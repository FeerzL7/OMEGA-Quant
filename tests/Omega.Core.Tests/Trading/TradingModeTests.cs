using Omega.Core.Configuration;
using Omega.Core.Trading;

namespace Omega.Core.Tests.Trading;

public class TradingModeTests
{
    [Fact]
    public void Default_trading_mode_is_backtest()
    {
        Assert.Equal(TradingMode.Backtest, default(TradingMode));
    }

    [Fact]
    public void Trading_options_default_to_backtest_when_not_configured()
    {
        var options = new TradingOptions();

        Assert.Equal(TradingMode.Backtest, options.Mode);
    }
}
