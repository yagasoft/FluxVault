using FluxVault.Abstractions.Policies;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Tests;

public sealed class ResourceProfileSchedulerTests
{
    [Fact]
    public void Fast_balanced_and_quiet_profiles_have_increasing_debounce_delays()
    {
        var fast = ResourceProfileScheduler.GetDebounceDelay(ResourceProfile.Fast);
        var balanced = ResourceProfileScheduler.GetDebounceDelay(ResourceProfile.Balanced);
        var quiet = ResourceProfileScheduler.GetDebounceDelay(ResourceProfile.Quiet);

        Assert.True(fast < balanced);
        Assert.True(balanced < quiet);
        Assert.Equal(TimeSpan.FromSeconds(2), fast);
    }
}
