using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Core.Sync;

public sealed class FileSyncHydrationStore(string repositoryPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static FileSyncHydrationStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public Task RecordHydrationAsync(SyncHydrationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicWrite(HydrationPath(record.HydrationId), JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions), overwrite: true);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SyncHydrationRecord>> ListHydrationsAsync(CancellationToken cancellationToken = default)
    {
        var path = HydrationsPath();
        if (!Directory.Exists(path))
        {
            return [];
        }

        var records = new List<SyncHydrationRecord>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(file);
            records.Add(await JsonSerializer.DeserializeAsync<SyncHydrationRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Sync hydration metadata could not be read: {file}"));
        }

        return records
            .OrderByDescending(record => record.CompletedAtUtc)
            .ThenBy(record => record.HydrationId, StringComparer.Ordinal)
            .ToArray();
    }

    private string HydrationsPath() => Path.Combine(repositoryPath, "sync", "hydrations");

    private string HydrationPath(string hydrationId) => Path.Combine(HydrationsPath(), $"{SafeName(hydrationId)}.json");

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
