namespace FluxVault.Abstractions.Configuration;

public enum ShellIntegrationMode
{
    CloudFilesApi = 0,
    ProjFs = 1
}

public enum ShellHydrationPolicy
{
    OnDemand = 0,
    Manual = 1,
    AlwaysLocal = 2
}

public sealed record ShellIntegrationConfiguration(
    bool IsEnabled,
    ShellIntegrationMode Mode,
    string SyncRootPath,
    string DisplayName = "FluxVault",
    ShellHydrationPolicy HydrationPolicy = ShellHydrationPolicy.OnDemand,
    string PlaceholderStatePath = "")
{
    public static ShellIntegrationConfiguration CreateDefault(string programDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        return new ShellIntegrationConfiguration(
            IsEnabled: false,
            Mode: ShellIntegrationMode.CloudFilesApi,
            SyncRootPath: Path.Combine(programDataPath, "shell-integration", "sync-root"),
            PlaceholderStatePath: Path.Combine(programDataPath, "shell-integration", "state"));
    }

    public ShellIntegrationConfiguration Normalise(string programDataPath)
    {
        var syncRootPath = string.IsNullOrWhiteSpace(SyncRootPath)
            ? Path.Combine(programDataPath, "shell-integration", "sync-root")
            : SyncRootPath.Trim();
        var displayName = string.IsNullOrWhiteSpace(DisplayName)
            ? "FluxVault"
            : DisplayName.Trim();
        var placeholderStatePath = string.IsNullOrWhiteSpace(PlaceholderStatePath)
            ? Path.Combine(programDataPath, "shell-integration", "state")
            : PlaceholderStatePath.Trim();
        return this with
        {
            SyncRootPath = syncRootPath,
            DisplayName = displayName,
            PlaceholderStatePath = placeholderStatePath
        };
    }
}
