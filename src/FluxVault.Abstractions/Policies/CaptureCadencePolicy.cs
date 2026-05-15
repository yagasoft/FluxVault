namespace FluxVault.Abstractions.Policies;

public sealed record CaptureCadencePolicy(
    TimeSpan WatcherPollInterval,
    TimeSpan PeriodicReconciliationInterval,
    TimeSpan FastDebounce,
    TimeSpan BalancedDebounce,
    TimeSpan QuietDebounce,
    TimeSpan FastMaxHotFileDelay,
    TimeSpan BalancedMaxHotFileDelay,
    TimeSpan QuietMaxHotFileDelay,
    TimeSpan MinimumSameFileCaptureInterval,
    int MaximumConcurrentCaptures,
    int WatcherEventBacklogLimit = 4096)
{
    public static CaptureCadencePolicy CreateDefault()
    {
        return new CaptureCadencePolicy(
            WatcherPollInterval: TimeSpan.FromSeconds(5),
            PeriodicReconciliationInterval: TimeSpan.FromMinutes(10),
            FastDebounce: TimeSpan.FromSeconds(2),
            BalancedDebounce: TimeSpan.FromSeconds(8),
            QuietDebounce: TimeSpan.FromSeconds(30),
            FastMaxHotFileDelay: TimeSpan.FromSeconds(30),
            BalancedMaxHotFileDelay: TimeSpan.FromMinutes(2),
            QuietMaxHotFileDelay: TimeSpan.FromMinutes(10),
            MinimumSameFileCaptureInterval: TimeSpan.FromSeconds(15),
            MaximumConcurrentCaptures: 2,
            WatcherEventBacklogLimit: 4096);
    }

    public TimeSpan GetDebounce(ResourceProfile profile)
    {
        return profile switch
        {
            ResourceProfile.Fast => FastDebounce,
            ResourceProfile.Quiet => QuietDebounce,
            _ => BalancedDebounce
        };
    }

    public TimeSpan GetMaxHotFileDelay(ResourceProfile profile)
    {
        return profile switch
        {
            ResourceProfile.Fast => FastMaxHotFileDelay,
            ResourceProfile.Quiet => QuietMaxHotFileDelay,
            _ => BalancedMaxHotFileDelay
        };
    }
}
