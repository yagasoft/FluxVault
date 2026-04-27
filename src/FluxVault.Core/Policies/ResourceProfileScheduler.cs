using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public static class ResourceProfileScheduler
{
    public static TimeSpan GetDebounceDelay(ResourceProfile profile)
    {
        return CaptureCadencePolicy.CreateDefault().GetDebounce(profile);
    }
}
