using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Abstractions.Ipc;

public sealed record FluxVaultIpcRequest(
    FluxVaultIpcCommand Command,
    FluxVaultConfiguration? Configuration,
    string? VersionId,
    string? OutputPath,
    string? ExportPath,
    string? MirrorNodeId = null,
    string? ConflictId = null,
    SyncConflictAction? ConflictAction = null,
    string? SourcePath = null,
    bool IsDirectory = false,
    RestoreSelectionDestinationMode? DestinationMode = null,
    bool OverwriteConfirmed = false,
    string? ProfileId = null,
    string? ProfileDisplayName = null,
    string? SourceProfileId = null,
    FluxVaultStatusDetailLevel StatusDetailLevel = FluxVaultStatusDetailLevel.Full)
{
    public string? DestinationPath => OutputPath;

    public static FluxVaultIpcRequest GetStatus(
        string? profileId = null,
        FluxVaultStatusDetailLevel statusDetailLevel = FluxVaultStatusDetailLevel.Full)
    {
        return new FluxVaultIpcRequest(
            FluxVaultIpcCommand.GetStatus,
            null,
            null,
            null,
            null,
            ProfileId: profileId,
            StatusDetailLevel: statusDetailLevel);
    }

    public static FluxVaultIpcRequest SaveConfiguration(FluxVaultConfiguration configuration, string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.SaveConfiguration, configuration, null, null, null, ProfileId: profileId);
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

    public static FluxVaultIpcRequest RestoreVersionPreview(string versionId)
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RestoreVersionPreview, null, versionId, null, null);
    }

    public static FluxVaultIpcRequest PreviewRestoreSelection(
        string sourcePath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string? destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new FluxVaultIpcRequest(
            FluxVaultIpcCommand.PreviewRestoreSelection,
            null,
            null,
            destinationPath,
            null,
            SourcePath: sourcePath,
            IsDirectory: isDirectory,
            DestinationMode: destinationMode);
    }

    public static FluxVaultIpcRequest RunRestoreSelection(
        string sourcePath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string? destinationPath,
        bool overwriteConfirmed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new FluxVaultIpcRequest(
            FluxVaultIpcCommand.RunRestoreSelection,
            null,
            null,
            destinationPath,
            null,
            SourcePath: sourcePath,
            IsDirectory: isDirectory,
            DestinationMode: destinationMode,
            OverwriteConfirmed: overwriteConfirmed);
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

    public static FluxVaultIpcRequest GetActivity()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.GetActivity, null, null, null, null);
    }

    public static FluxVaultIpcRequest ListBlockedFiles()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.ListBlockedFiles, null, null, null, null);
    }

    public static FluxVaultIpcRequest SetProtectionPaused()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.SetProtectionPaused, null, null, null, null);
    }

    public static FluxVaultIpcRequest GetRepositoryHealth()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.GetRepositoryHealth, null, null, null, null);
    }

    public static FluxVaultIpcRequest RunRepositoryScrub()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RunRepositoryScrub, null, null, null, null);
    }

    public static FluxVaultIpcRequest RunRestoreRehearsal()
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RunRestoreRehearsal, null, null, null, null);
    }

    public static FluxVaultIpcRequest PreviewMirrorRepair(string? mirrorNodeId = null)
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.PreviewMirrorRepair, null, null, null, null, mirrorNodeId);
    }

    public static FluxVaultIpcRequest RunMirrorRepair(string? mirrorNodeId = null)
    {
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RunMirrorRepair, null, null, null, null, mirrorNodeId);
    }

    public static FluxVaultIpcRequest PreviewMirrorDrain(string mirrorNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorNodeId);
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.PreviewMirrorDrain, null, null, null, null, mirrorNodeId);
    }

    public static FluxVaultIpcRequest RunMirrorDrain(string mirrorNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorNodeId);
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.RunMirrorDrain, null, null, null, null, mirrorNodeId);
    }

    public static FluxVaultIpcRequest ResolveConflict(string conflictId, SyncConflictAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conflictId);
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.ResolveConflict, null, null, null, null, ConflictId: conflictId, ConflictAction: action);
    }

    public static FluxVaultIpcRequest CreateProfile(string profileId, string displayName, FluxVaultConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new FluxVaultIpcRequest(
            FluxVaultIpcCommand.CreateProfile,
            configuration,
            null,
            null,
            null,
            ProfileId: profileId,
            ProfileDisplayName: displayName);
    }

    public static FluxVaultIpcRequest RenameProfile(string profileId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new FluxVaultIpcRequest(
            FluxVaultIpcCommand.RenameProfile,
            null,
            null,
            null,
            null,
            ProfileId: profileId,
            ProfileDisplayName: displayName);
    }

    public static FluxVaultIpcRequest DuplicateProfile(string sourceProfileId, string profileId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new FluxVaultIpcRequest(
            FluxVaultIpcCommand.DuplicateProfile,
            null,
            null,
            null,
            null,
            ProfileId: profileId,
            ProfileDisplayName: displayName,
            SourceProfileId: sourceProfileId);
    }

    public static FluxVaultIpcRequest DeleteProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.DeleteProfile, null, null, null, null, ProfileId: profileId);
    }

    public static FluxVaultIpcRequest SetActiveProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new FluxVaultIpcRequest(FluxVaultIpcCommand.SetActiveProfile, null, null, null, null, ProfileId: profileId);
    }
}

public enum FluxVaultStatusDetailLevel
{
    Full = 0,
    Fast = 1
}
