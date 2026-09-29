using Omega.Core.Trading;

namespace Omega.Core.Tests.Trading;

public class SignalDirectionTests
{
    [Fact]
    public void Default_value_is_not_a_valid_direction()
    {
        // An uninitialized direction must never be read as LONG (or any other signal).
        Assert.False(Enum.IsDefined(default(SignalDirection)));
    }
}
