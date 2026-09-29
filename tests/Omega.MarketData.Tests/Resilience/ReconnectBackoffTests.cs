using Omega.MarketData.Resilience;

namespace Omega.MarketData.Tests.Resilience;

public class ReconnectBackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    [Fact]
    public void Delay_doubles_per_attempt_within_the_jitter_band()
    {
        var lowest = new ReconnectBackoff(Initial, Max, new FixedRandom(0.0));
        var middle = new ReconnectBackoff(Initial, Max, new FixedRandom(0.5));

        Assert.Equal(TimeSpan.FromSeconds(0.5), lowest.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(4), lowest.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(6), middle.GetDelay(3));
    }

    [Fact]
    public void Delay_never_exceeds_the_maximum()
    {
        var highest = new ReconnectBackoff(Initial, Max, new FixedRandom(0.999_999));

        Assert.InRange(highest.GetDelay(10), TimeSpan.FromSeconds(30), Max);
        Assert.InRange(highest.GetDelay(10_000), TimeSpan.FromSeconds(30), Max);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectBackoff(TimeSpan.Zero, Max, Random.Shared));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectBackoff(Max, Initial, Random.Shared));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectBackoff(Initial, Max, Random.Shared).GetDelay(-1));
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
