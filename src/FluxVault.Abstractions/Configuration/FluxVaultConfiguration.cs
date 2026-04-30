namespace FluxVault.Abstractions.Configuration;

using FluxVault.Abstractions.Policies;

public sealed record FluxVaultConfiguration(
    string RepositoryPath,
    string? MirrorPath,
    bool IsEnabled,
    IReadOnlyList<WatchedFolderConfiguration> WatchedFolders,
    RetentionPolicy RetentionPolicy = null!,
    CaptureCadencePolicy CaptureCadencePolicy = null!,
    CodecPolicy CodecPolicy = null!,
    IReadOnlyList<ProtectionSelectionRule> SelectionRules = null!,
    IReadOnlyList<ProtectionExclusionRule> ExclusionRules = null!,
    RepositoryMaintenancePolicy RepositoryMaintenancePolicy = null!,
    WorkloadPolicyConfiguration WorkloadPolicy = null!,
    MirrorSetConfiguration MirrorSet = null!)
{
    public static FluxVaultConfiguration CreateDefault(string programDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        return new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(programDataPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            RetentionPolicy: RetentionPolicy.CreateDefault(),
            CaptureCadencePolicy: CaptureCadencePolicy.CreateDefault(),
            CodecPolicy: CodecPolicy.CreateDefault(),
            SelectionRules: [],
            ExclusionRules: [],
            RepositoryMaintenancePolicy: RepositoryMaintenancePolicy.CreateDefault(),
            WorkloadPolicy: WorkloadPolicyConfiguration.CreateDefault(),
            MirrorSet: MirrorSetConfiguration.CreateDefault());
    }
}
