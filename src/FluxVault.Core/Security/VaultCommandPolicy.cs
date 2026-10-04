using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;

namespace FluxVault.Core.Security;

public sealed record VaultCommandRequirement(VaultPermission Permissions, bool CreatesVault = false);

public static class VaultCommandPolicy
{
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
            FluxVaultIpcCommand.GetStatus or FluxVaultIpcCommand.ListVersions or FluxVaultIpcCommand.InspectVersion or
            FluxVaultIpcCommand.GetActivity or FluxVaultIpcCommand.ListBlockedFiles or FluxVaultIpcCommand.GetSyncStatus or
            FluxVaultIpcCommand.GetRepositoryHealth or FluxVaultIpcCommand.GetPerformance or FluxVaultIpcCommand.ExportDiagnostics or
            FluxVaultIpcCommand.PreviewRetention or FluxVaultIpcCommand.SetActiveProfile or
            FluxVaultIpcCommand.ListVaults or FluxVaultIpcCommand.GetOperationStatus => new(VaultPermission.ReadHistory),
            FluxVaultIpcCommand.SetVaultAccess => new(VaultPermission.ManageAccess),
            FluxVaultIpcCommand.RestoreVersion or FluxVaultIpcCommand.RestoreVersionPreview or
            FluxVaultIpcCommand.PreviewRestoreSelection or FluxVaultIpcCommand.RunRestoreSelection => new(VaultPermission.Recover),
            FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow or FluxVaultIpcCommand.SetProtectionPaused or
            FluxVaultIpcCommand.RenameProfile => new(VaultPermission.ManageProtection),
            FluxVaultIpcCommand.ResolveConflict => new(VaultPermission.ManageProtection | VaultPermission.Recover | VaultPermission.DeleteHistory),
            FluxVaultIpcCommand.DuplicateProfile => new(VaultPermission.ReadHistory | VaultPermission.ManageProtection, CreatesVault: true),
            FluxVaultIpcCommand.CreateProfile => new(VaultPermission.ManageProtection, CreatesVault: true),
            FluxVaultIpcCommand.PreviewMirrorRebalance or FluxVaultIpcCommand.RunMirrorRebalance or FluxVaultIpcCommand.RunRepositoryScrub or
            FluxVaultIpcCommand.RunRestoreRehearsal or FluxVaultIpcCommand.PreviewMirrorRepair or FluxVaultIpcCommand.RunMirrorRepair => new(VaultPermission.Maintain),
            FluxVaultIpcCommand.RunRetentionNow or FluxVaultIpcCommand.PreviewMirrorDrain or FluxVaultIpcCommand.RunMirrorDrain =>
                new(VaultPermission.Maintain | VaultPermission.DeleteHistory),
            FluxVaultIpcCommand.DeleteProfile => new(VaultPermission.ManageProtection | VaultPermission.Maintain | VaultPermission.DeleteHistory),
            _ => null!
        };
        return policy is not null;
    }
}
