using System.Text.Json;
using FluxVault.Abstractions.ChangeTracking;

namespace FluxVault.Core.ChangeTracking;

public sealed class FileUsnJournalCheckpointStore(string statePath) : IUsnJournalCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyList<UsnJournalCheckpoint>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(statePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<IReadOnlyList<UsnJournalCheckpoint>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? [];
    }

    public async Task SaveAsync(IReadOnlyList<UsnJournalCheckpoint> checkpoints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        var directory = Path.GetDirectoryName(statePath) ?? throw new InvalidOperationException("USN state path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(
            tempPath,
            JsonSerializer.SerializeToUtf8Bytes(checkpoints, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, statePath, overwrite: true);
    }
}
