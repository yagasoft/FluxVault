using FluxVault.Abstractions.Configuration;

namespace FluxVault.Abstractions.Ipc;

public sealed record FluxVaultIpcRequest(
    FluxVaultIpcCommand Command,
    FluxVaultConfiguration? Configuration,
    string? VersionId,
    string? OutputPath,
    string? ExportPath)
{
    public static FluxVaultIpcRequest GetStatus()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.GetStatus, null, null, null, null);
    }

    public static FluxVaultIpcRequest SaveConfiguration(FluxVaultConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.SaveConfiguration, configuration, null, null, null);
    }

    public static FluxVaultIpcRequest RunBackupNow()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RunBackupNow, null, null, null, null);
    }

    public static FluxVaultIpcRequest ListVersions()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.ListVersions, null, null, null, null);
    }

    public static FluxVaultIpcRequest InspectVersion(string versionId)
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.InspectVersion, null, versionId, null, null);
    }

    public static FluxVaultIpcRequest RestoreVersion(string versionId, string outputPath)
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RestoreVersion, null, versionId, outputPath, null);
    }

    public static FluxVaultIpcRequest ExportDiagnostics(string exportPath)
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.ExportDiagnostics, null, null, null, exportPath);
    }

    public static FluxVaultIpcRequest PreviewRetention()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.PreviewRetention, null, null, null, null);
    }

    public static FluxVaultIpcRequest RunRetentionNow()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RunRetentionNow, null, null, null, null);
    }
}
