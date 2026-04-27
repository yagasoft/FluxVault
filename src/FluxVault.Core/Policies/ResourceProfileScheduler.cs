using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public static class ResourceProfileScheduler
{
    public static TimeSpan GetDebounceDelay(ResourceProfile profile)
    {
        return profile switch
        {
            ResourceProfile.Fast => TimeSpan.FromSeconds(2),
            ResourceProfile.Balanced => TimeSpan.FromSeconds(8),
            ResourceProfile.Quiet => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(8)
        };
    }
}
