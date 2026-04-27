using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Ipc;

public sealed record FluxVaultServiceStatus(
    bool IsServiceRunning,
    FluxVaultConfiguration Configuration,
    string LastMessage,
    DateTimeOffset? LastCaptureUtc,
    IReadOnlyList<WatchedFolderRuntimeStatus> WatchedFolders,
    IReadOnlyList<RepositoryVersionSummary> RecentVersions,
    DurableChangeRuntimeStatus? DurableChange = null);

public sealed record WatchedFolderRuntimeStatus(
    string Id,
    string Path,
    bool Exists,
    bool IsEnabled,
    string Status,
    string DurableChangeStatus = "Unknown");
