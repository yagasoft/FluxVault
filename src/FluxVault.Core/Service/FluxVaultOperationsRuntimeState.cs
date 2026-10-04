using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Diagnostics;

namespace FluxVault.Core.Service;

/// <summary>Per-vault runtime observations. Never owns a caller, configuration or source adapter.</summary>
public sealed class FluxVaultOperationsRuntimeState
{
    internal ProtectionRuntimeCoordinator Coordinator { get; } = new();
    internal TelemetryCollector Telemetry { get; } = new(TimeProvider.System);
    internal Lock RuntimeGate { get; } = new();
    internal Lock ActiveCaptureGate { get; } = new();
    internal SemaphoreSlim MutatingOperationGate { get; } = new(1, 1);
    internal SemaphoreSlim BackupOperationGate { get; } = new(1, 1);
    internal Dictionary<string, CaptureRuntimeStatus> CaptureStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, WatcherRuntimeStatus> WatcherStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, CancellationTokenSource> ActiveCaptureTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> RemovedCapturePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal DateTimeOffset? LastCaptureUtc;
    internal string LastMessage = "Ready";
    internal IReadOnlyList<string> LastMirrorWarnings = [];
    internal DurableChangeRuntimeStatus? DurableChange;
    internal RepositoryRetentionResult? LastRetention;
    internal IReadOnlyList<RepositoryVersionSummary>? RecentVersionStatusCache;
    internal DateTimeOffset RecentVersionStatusCacheUtc;
    internal (string RepositoryPath, MetadataStoreConfiguration MetadataStore)? RecentVersionStatusCacheIdentity;
    internal IReadOnlyList<RepositoryVersionSummary>? TrackedEntryStatusCache;
    internal DateTimeOffset TrackedEntryStatusCacheUtc;
    internal (string RepositoryPath, MetadataStoreConfiguration MetadataStore)? TrackedEntryStatusCacheIdentity;
    internal long RepositoryStatusCacheGeneration;
    internal BackupRuntimeStatus BackupRuntime = new(false, "Idle", null, null, null, 0, 0, 0, 0, 0, 0, 0);
    internal BackgroundWorkRuntimeStatus RepositoryMaintenanceRuntime = new("Repository maintenance", "Waiting", 0, 0, "No maintenance running");
}
