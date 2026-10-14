namespace FluxVault.Abstractions.Configuration;

/// <summary>Versioned separately from the predecessor-readable base configuration.</summary>
public sealed record AutomaticProtectionPolicy(bool Enabled = false);

/// <summary>Finite internal work bounds; cadence remains in CaptureCadencePolicy.</summary>
public sealed record AutomaticProtectionRuntimePolicy(
    int MaximumPendingPaths = 20000,
    int MaximumReconciliationEntries = 100000,
    int DispatchBatchSize = 32,
    int RetrySeconds = 15,
    int SessionObservationSeconds = 2,
    int RuntimeOwnerCommandTimeoutSeconds = 5)
{
    public void Validate()
    {
        if (MaximumPendingPaths is < 1 or > 100000 || MaximumReconciliationEntries is < 1 or > 1000000 ||
            DispatchBatchSize is < 1 or > 256 || RetrySeconds is < 1 or > 300 ||
            SessionObservationSeconds is < 1 or > 30 || RuntimeOwnerCommandTimeoutSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(AutomaticProtectionRuntimePolicy));
    }
}
