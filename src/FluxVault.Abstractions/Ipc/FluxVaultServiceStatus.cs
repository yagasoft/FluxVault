using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Abstractions.Ipc;

public sealed record FluxVaultServiceStatus(
    bool IsServiceRunning,
    FluxVaultConfiguration Configuration,
    string LastMessage,
    DateTimeOffset? LastCaptureUtc,
    IReadOnlyList<WatchedFolderRuntimeStatus> WatchedFolders,
    IReadOnlyList<RepositoryVersionSummary> RecentVersions,
    RepositoryRetentionResult? LastRetention = null,
    DurableChangeRuntimeStatus? DurableChange = null,
    IReadOnlyList<CaptureRuntimeStatus>? CaptureStatuses = null,
    RepositoryHealthSnapshot? RepositoryHealth = null,
    IReadOnlyList<string>? MirrorWarnings = null,
    DeviceIdentityRuntimeStatus? DeviceIdentity = null,
    SyncRuntimeStatus? Sync = null,
    PerformanceWorkspaceRuntimeStatus? PerformanceWorkspace = null,
    ShellIntegrationRuntimeStatus? ShellIntegration = null);

public sealed record WatchedFolderRuntimeStatus(
    string Id,
    string Path,
    bool Exists,
    bool IsEnabled,
    string Status,
    string DurableChangeStatus = "Unknown");

public sealed record DeviceIdentityRuntimeStatus(
    string DeviceId,
    string DisplayName,
    IReadOnlyList<TrustedDeviceRuntimeStatus> TrustedDevices);

public sealed record TrustedDeviceRuntimeStatus(
    string DeviceId,
    string DisplayName,
    DeviceTrustState TrustState,
    DateTimeOffset TrustedAtUtc,
    DateTimeOffset? LastSeenAtUtc);

public sealed record SyncRuntimeStatus(
    string LocalDeviceId,
    IReadOnlyList<PeerHeadRecord> PeerHeads,
    IReadOnlyList<PeerCursorRecord> Cursors,
    IReadOnlyList<SyncMappingRecord>? Mappings = null,
    IReadOnlyList<SyncAppliedVersionRecord>? AppliedRemoteVersions = null,
    IReadOnlyList<SyncHydrationRecord>? Hydrations = null,
    IReadOnlyList<SyncConflictRecord>? Conflicts = null);

public sealed record PerformanceWorkspaceRuntimeStatus(
    bool IsEnabled,
    PerformanceWorkspaceMode Mode,
    string WorkspacePath,
    int CacheSizeMegabytes,
    string MountName,
    string SetupScriptPath,
    string ManifestPath,
    string Status,
    bool IsDriverCheckDeferred);

public sealed record ShellIntegrationRuntimeStatus(
    bool IsEnabled,
    ShellIntegrationMode Mode,
    string SyncRootPath,
    string DisplayName,
    ShellHydrationPolicy HydrationPolicy,
    string PlaceholderStatePath,
    string RegisterScriptPath,
    string ManifestPath,
    string Status,
    bool IsRegistrationDeferred,
    bool IsPlaceholderCreationDeferred);
