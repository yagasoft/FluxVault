namespace FluxVault.Abstractions.Configuration;

public sealed record RepositoryMaintenancePolicy(
    bool IsEnabled,
    TimeSpan Interval,
    bool AutoRepairFromMirror,
    int RestoreRehearsalVersionCount,
    bool RunAutomatically = false)
{
    public static RepositoryMaintenancePolicy CreateDefault()
    {
        return new RepositoryMaintenancePolicy(
            IsEnabled: true,
            Interval: TimeSpan.FromHours(24),
            AutoRepairFromMirror: true,
            RestoreRehearsalVersionCount: 3,
            RunAutomatically: false);
    }
}
