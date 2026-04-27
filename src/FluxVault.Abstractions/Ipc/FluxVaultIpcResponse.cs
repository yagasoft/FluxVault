using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Ipc;

public sealed record FluxVaultIpcResponse(
    bool Success,
    string? ErrorMessage,
    FluxVaultServiceStatus? Status,
    BackupRunSummary? Backup,
    IReadOnlyList<RepositoryVersionSummary>? Versions,
    RepositoryInspection? Inspection,
    string? OutputPath)
{
    public static FluxVaultIpcResponse Ok()
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, null);
    }

    public static FluxVaultIpcResponse WithStatus(FluxVaultServiceStatus status)
    {
        return new FluxVaultIpcResponse(true, null, status, null, null, null, null);
    }

    public static FluxVaultIpcResponse WithBackup(BackupRunSummary backup)
    {
        return new FluxVaultIpcResponse(true, null, null, backup, null, null, null);
    }

    public static FluxVaultIpcResponse WithVersions(IReadOnlyList<RepositoryVersionSummary> versions)
    {
        return new FluxVaultIpcResponse(true, null, null, null, versions, null, null);
    }

    public static FluxVaultIpcResponse WithInspection(RepositoryInspection inspection)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, inspection, null);
    }

    public static FluxVaultIpcResponse WithOutputPath(string outputPath)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, outputPath);
    }

    public static FluxVaultIpcResponse Failure(string errorMessage)
    {
        return new FluxVaultIpcResponse(false, errorMessage, null, null, null, null, null);
    }
}
