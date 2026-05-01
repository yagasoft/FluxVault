using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Core.Sync;

public sealed class FileSyncConflictStore(string repositoryPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static FileSyncConflictStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public Task<SyncConflictRecord> RecordConflictAsync(
        SyncConflictRecord conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicWrite(ConflictPath(conflict.ConflictId), JsonSerializer.SerializeToUtf8Bytes(conflict, JsonOptions), overwrite: true);
        return Task.FromResult(conflict);
    }

    public async Task<SyncConflictRecord> ResolveConflictAsync(
        string conflictId,
        SyncConflictAction action,
        CancellationToken cancellationToken = default)
    {
        var conflict = await ReadConflictAsync(conflictId, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Sync conflict {conflictId} does not exist.");
        var resolved = conflict with
        {
            Status = SyncConflictStatus.Resolved,
            ResolutionAction = action,
            ResolvedAtUtc = DateTimeOffset.UtcNow
        };
        await RecordConflictAsync(resolved, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    public async Task<SyncConflictRecord?> ReadConflictAsync(string conflictId, CancellationToken cancellationToken = default)
    {
        var path = ConflictPath(conflictId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SyncConflictRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Sync conflict metadata could not be read: {path}");
    }

    public async Task<IReadOnlyList<SyncConflictRecord>> ListConflictsAsync(CancellationToken cancellationToken = default)
    {
        var path = ConflictsPath();
        if (!Directory.Exists(path))
        {
            return [];
        }

        var records = new List<SyncConflictRecord>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(file);
            records.Add(await JsonSerializer.DeserializeAsync<SyncConflictRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Sync conflict metadata could not be read: {file}"));
        }

        return records
            .OrderByDescending(record => record.DetectedAtUtc)
            .ThenBy(record => record.ConflictId, StringComparer.Ordinal)
            .ToArray();
    }

    private string ConflictsPath() => Path.Combine(repositoryPath, "sync", "conflicts");

    private string ConflictPath(string conflictId) => Path.Combine(ConflictsPath(), $"{SafeName(conflictId)}.json");

    private static string SafeName(string value)
    {
        return string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_'));
    }

    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite);
    }
}
