using Omega.Core.Trading;

namespace Omega.Core.Tests.Trading;

public class SignalTests
{
    [Fact]
    public void No_trade_always_carries_a_defined_reason()
    {
        var signal = Signal.NoTrade(NoTradeReason.ExpectedValueTooLow, "EV below costs.");

        Assert.Equal(SignalDirection.NoTrade, signal.Direction);
        Assert.Equal(NoTradeReason.ExpectedValueTooLow, signal.Reason);
        Assert.Throws<ArgumentOutOfRangeException>(() => Signal.NoTrade(default));
    }

    [Fact]
    public void Long_requires_a_positive_stop_and_a_target_above_it()
    {
        var signal = Signal.Long(90m, 110m);

        Assert.Equal((90m, 110m), (signal.StopLossPrice, signal.TakeProfitPrice));
        Assert.Throws<ArgumentOutOfRangeException>(() => Signal.Long(0m));
        Assert.Throws<ArgumentException>(() => Signal.Long(90m, 85m));
    }

    [Fact]
    public void Only_no_trade_has_a_reason()
    {
        Assert.Null(Signal.Hold().Reason);
        Assert.Null(Signal.Short().Reason);
        Assert.Null(Signal.Long(1m).Reason);
    }
}
