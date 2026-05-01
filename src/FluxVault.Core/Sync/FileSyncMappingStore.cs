using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Core.Sync;

public sealed class FileSyncMappingStore(string repositoryPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static FileSyncMappingStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<SyncMappingRecord> ProposeMappingAsync(
        string sourceDeviceId,
        string sourcePath,
        string localDeviceId,
        string? proposedLocalPath,
        CancellationToken cancellationToken = default)
    {
        var mappingId = CreateMappingId(sourceDeviceId, sourcePath, localDeviceId);
        var path = MappingPath(mappingId);
        if (File.Exists(path))
        {
            return await ReadJsonAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var mapping = new SyncMappingRecord(
            mappingId,
            sourceDeviceId.Trim(),
            sourcePath.Trim(),
            localDeviceId.Trim(),
            string.IsNullOrWhiteSpace(proposedLocalPath) ? null : proposedLocalPath.Trim(),
            SyncMappingStatus.PendingConfirmation,
            DateTimeOffset.UtcNow);
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(mapping, JsonOptions), overwrite: false);
        return mapping;
    }

    public async Task<SyncMappingRecord> ConfirmMappingAsync(
        string mappingId,
        string confirmedLocalPath,
        string confirmedByDeviceId,
        CancellationToken cancellationToken = default)
    {
        var existing = await ReadMappingAsync(mappingId, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Sync mapping {mappingId} does not exist.");
        var confirmed = existing with
        {
            LocalPath = confirmedLocalPath.Trim(),
            Status = SyncMappingStatus.Confirmed,
            ConfirmedAtUtc = DateTimeOffset.UtcNow,
            ConfirmedByDeviceId = confirmedByDeviceId.Trim()
        };
        AtomicWrite(MappingPath(mappingId), JsonSerializer.SerializeToUtf8Bytes(confirmed, JsonOptions), overwrite: true);
        return confirmed;
    }

    public async Task<SyncMappingRecord> BlockMappingAsync(
        string mappingId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var existing = await ReadMappingAsync(mappingId, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Sync mapping {mappingId} does not exist.");
        var blocked = existing with
        {
            Status = SyncMappingStatus.Blocked,
            Notes = notes
        };
        AtomicWrite(MappingPath(mappingId), JsonSerializer.SerializeToUtf8Bytes(blocked, JsonOptions), overwrite: true);
        return blocked;
    }

    public async Task<SyncMappingRecord?> ReadMappingAsync(
        string mappingId,
        CancellationToken cancellationToken = default)
    {
        var path = MappingPath(mappingId);
        return File.Exists(path)
            ? await ReadJsonAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<SyncMappingRecord>> ListMappingsAsync(CancellationToken cancellationToken = default)
    {
        var mappingsPath = MappingsPath();
        if (!Directory.Exists(mappingsPath))
        {
            return [];
        }

        var mappings = new List<SyncMappingRecord>();
        foreach (var path in Directory.EnumerateFiles(mappingsPath, "*.json"))
        {
            mappings.Add(await ReadJsonAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return mappings
            .OrderBy(mapping => mapping.SourceDeviceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mapping => mapping.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mapping => mapping.LocalDeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<SyncMappingRecord> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SyncMappingRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Sync mapping could not be read: {path}");
    }

    private string MappingsPath()
    {
        return Path.Combine(repositoryPath, "sync", "mappings");
    }

    private string MappingPath(string mappingId)
    {
        return Path.Combine(MappingsPath(), $"{SafeName(mappingId)}.json");
    }

    private static string CreateMappingId(string sourceDeviceId, string sourcePath, string localDeviceId)
    {
        var material = $"{sourceDeviceId.Trim().ToUpperInvariant()}|{sourcePath.Trim().ToUpperInvariant()}|{localDeviceId.Trim().ToUpperInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"mapping-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
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
