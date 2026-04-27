namespace FluxVault.Abstractions.Configuration;

using FluxVault.Abstractions.Policies;

public sealed record FluxVaultConfiguration(
    string RepositoryPath,
    string? MirrorPath,
    bool IsEnabled,
    IReadOnlyList<WatchedFolderConfiguration> WatchedFolders,
    RetentionPolicy RetentionPolicy = null!)
{
    public static FluxVaultConfiguration CreateDefault(string programDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        return new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(programDataPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            RetentionPolicy: RetentionPolicy.CreateDefault());
    }
}
