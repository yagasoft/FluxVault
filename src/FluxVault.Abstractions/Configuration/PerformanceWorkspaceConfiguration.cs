namespace FluxVault.Abstractions.Configuration;

public enum PerformanceWorkspaceMode
{
    WinFsp = 0
}

public sealed record PerformanceWorkspaceConfiguration(
    bool IsEnabled,
    PerformanceWorkspaceMode Mode,
    string WorkspacePath,
    int CacheSizeMegabytes = 1024,
    string MountName = "FluxVaultWorkspace")
{
    public static PerformanceWorkspaceConfiguration CreateDefault(string programDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        return new PerformanceWorkspaceConfiguration(
            IsEnabled: false,
            Mode: PerformanceWorkspaceMode.WinFsp,
            WorkspacePath: Path.Combine(programDataPath, "performance-workspace"));
    }

    public PerformanceWorkspaceConfiguration Normalise(string programDataPath)
    {
        var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath)
            ? Path.Combine(programDataPath, "performance-workspace")
            : WorkspacePath.Trim();
        var mountName = string.IsNullOrWhiteSpace(MountName)
            ? "FluxVaultWorkspace"
            : MountName.Trim();
        return this with
        {
            WorkspacePath = workspacePath,
            MountName = mountName,
            CacheSizeMegabytes = Math.Max(128, CacheSizeMegabytes)
        };
    }
}
