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
    internal Func<CancellationToken, Task>? AfterReadOpened { get; init; }
    internal Func<CancellationToken, Task>? BeforePublish { get; init; }
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

        // An open reader owns the old snapshot while a writer publishes its replacement.
        await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (AfterReadOpened is not null) await AfterReadOpened(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<RepositoryMaintenanceState>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? RepositoryMaintenanceState.Empty;
    }

    public async Task SaveAsync(RepositoryMaintenanceState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(statePath) ?? throw new InvalidOperationException("Maintenance state path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (BeforePublish is not null) await BeforePublish(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(statePath)) File.Replace(tempPath, statePath, destinationBackupFileName: null);
            else File.Move(tempPath, statePath);
        }
        finally
        {
            if (ownsTemporary) File.Delete(tempPath);
        }
    }
}
