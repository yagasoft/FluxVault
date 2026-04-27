namespace FluxVault.Abstractions.Configuration;

public sealed record FluxVaultConfiguration(
    string RepositoryPath,
    string? MirrorPath,
    bool IsEnabled,
    IReadOnlyList<WatchedFolderConfiguration> WatchedFolders)
{
    public static FluxVaultConfiguration CreateDefault(string programDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        return new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(programDataPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: []);
    }
}
