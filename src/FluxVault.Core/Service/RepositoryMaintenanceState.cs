using System.Text.Json;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Service;

public sealed record RepositoryMaintenanceState(
    DateTimeOffset? LastMaintenanceUtc,
    RepositoryHealthSnapshot? LastHealth,
    RepositoryScrubReport? LastScrub,
    RestoreRehearsalReport? LastRestoreRehearsal,
    MirrorRepairReport? LastMirrorRepair = null,
    MirrorRebalancePreviewReport? LastMirrorRebalance = null)
{
    public static RepositoryMaintenanceState Empty { get; } = new(null, null, null, null, null, null);
}

public interface IRepositoryMaintenanceStateStore
{
    Task<RepositoryMaintenanceState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RepositoryMaintenanceState state, CancellationToken cancellationToken = default);
}

public sealed class FileRepositoryMaintenanceStateStore(string statePath) : IRepositoryMaintenanceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<RepositoryMaintenanceState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(statePath))
        {
            return RepositoryMaintenanceState.Empty;
        }

        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<RepositoryMaintenanceState>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? RepositoryMaintenanceState.Empty;
    }

    public async Task SaveAsync(RepositoryMaintenanceState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(statePath) ?? throw new InvalidOperationException("Maintenance state path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tempPath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(tempPath, statePath, overwrite: true);
    }
}
