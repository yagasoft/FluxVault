using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Core.Sync;

public sealed class FileSyncApplicationStore(string repositoryPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static FileSyncApplicationStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public Task RecordAppliedVersionAsync(
        SyncAppliedVersionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicWrite(AppliedPath(record.SourceDeviceId, record.SourceOperationId), JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions), overwrite: true);
        return Task.CompletedTask;
    }

    public Task<bool> HasAppliedSourceOperationAsync(
        string sourceDeviceId,
        string sourceOperationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(AppliedPath(sourceDeviceId, sourceOperationId)));
    }

    public async Task<IReadOnlyList<SyncAppliedVersionRecord>> ListAppliedVersionsAsync(CancellationToken cancellationToken = default)
    {
        var appliedPath = AppliedPath();
        if (!Directory.Exists(appliedPath))
        {
            return [];
        }

        var records = new List<SyncAppliedVersionRecord>();
        foreach (var path in Directory.EnumerateFiles(appliedPath, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(path);
            records.Add(await JsonSerializer.DeserializeAsync<SyncAppliedVersionRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Sync applied-version metadata could not be read: {path}"));
        }

        return records
            .OrderBy(record => record.SourceDeviceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.SourceOperationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string AppliedPath()
    {
        return Path.Combine(repositoryPath, "sync", "applied");
    }

    private string AppliedPath(string sourceDeviceId, string sourceOperationId)
    {
        return Path.Combine(AppliedPath(), SafeName(sourceDeviceId), $"{SafeName(sourceOperationId)}.json");
    }

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
