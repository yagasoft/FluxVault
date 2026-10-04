using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Security;

public sealed record VaultCommandRequirement(VaultPermission Permissions);

public static class VaultCommandPolicy
{
    public static bool TryGet(FluxVaultIpcRequest request, FluxVaultConfiguration currentConfiguration, out VaultCommandRequirement policy)
    {
        ArgumentNullException.ThrowIfNull(currentConfiguration);
        if (!TryGet(request, out policy)) return false;
        if (request.Command == FluxVaultIpcCommand.RunBackupNow && currentConfiguration.RetentionPolicy.IsEnabled ||
            request.Command == FluxVaultIpcCommand.SaveConfiguration && request.Configuration is { } proposed &&
            proposed.RetentionPolicy != currentConfiguration.RetentionPolicy)
            policy = policy with { Permissions = policy.Permissions | VaultPermission.Maintain | VaultPermission.DeleteHistory };
        return true;
    }

    public static bool TryGet(FluxVaultIpcRequest request, out VaultCommandRequirement policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGet(request.Command, out policy)) return false;
        if (request.Command == FluxVaultIpcCommand.SaveConfiguration && request.PurgeRemovedSelections)
            policy = policy with { Permissions = policy.Permissions | VaultPermission.Maintain | VaultPermission.DeleteHistory };
        return true;
    }

    public static bool TryGet(FluxVaultIpcCommand command, out VaultCommandRequirement policy)
    {
        policy = command switch
        {
            FluxVaultIpcCommand.GetStatus or FluxVaultIpcCommand.ListVersions or FluxVaultIpcCommand.ListHistoryPage or FluxVaultIpcCommand.GetSnapshotPage or FluxVaultIpcCommand.ListCurrentEntriesPage or FluxVaultIpcCommand.InspectVersion or
            FluxVaultIpcCommand.GetActivity or FluxVaultIpcCommand.ListBlockedFiles or FluxVaultIpcCommand.GetSyncStatus or
            FluxVaultIpcCommand.GetRepositoryHealth or FluxVaultIpcCommand.GetPerformance or FluxVaultIpcCommand.ExportDiagnostics or
            FluxVaultIpcCommand.PreviewRetention or FluxVaultIpcCommand.GetOperationStatus => new(VaultPermission.ReadHistory),
            FluxVaultIpcCommand.SetVaultAccess => new(VaultPermission.ManageAccess),
            FluxVaultIpcCommand.RestoreVersion or FluxVaultIpcCommand.RestoreVersionPreview or
            FluxVaultIpcCommand.PreviewRestoreSelection or FluxVaultIpcCommand.RunRestoreSelection => new(VaultPermission.Recover),
            FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow or FluxVaultIpcCommand.SetProtectionPaused => new(VaultPermission.ManageProtection),
            FluxVaultIpcCommand.ResolveConflict => new(VaultPermission.ManageProtection | VaultPermission.Recover | VaultPermission.DeleteHistory),
            FluxVaultIpcCommand.PreviewMirrorRebalance or FluxVaultIpcCommand.RunMirrorRebalance or FluxVaultIpcCommand.RunRepositoryScrub or
            FluxVaultIpcCommand.RunRestoreRehearsal or FluxVaultIpcCommand.PreviewMirrorRepair or FluxVaultIpcCommand.RunMirrorRepair => new(VaultPermission.Maintain),
            FluxVaultIpcCommand.RunRetentionNow or FluxVaultIpcCommand.PreviewMirrorDrain or FluxVaultIpcCommand.RunMirrorDrain =>
                new(VaultPermission.Maintain | VaultPermission.DeleteHistory),
            _ => null!
        };
        return policy is not null;
    }
}
