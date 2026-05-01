using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Core.Sync;

public sealed class FilePeerSyncJournal(string repositoryPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static FilePeerSyncJournal()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<PeerOperationRecord> AppendOperationAsync(
        DeviceIdentityConfiguration localDevice,
        PeerOperationDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localDevice);
        ArgumentNullException.ThrowIfNull(draft);
        var operationId = string.IsNullOrWhiteSpace(draft.OperationId)
            ? Guid.NewGuid().ToString("N")
            : draft.OperationId.Trim();
        var operationsPath = OperationsPath(localDevice.DeviceId);
        Directory.CreateDirectory(operationsPath);
        if (Directory.EnumerateFiles(operationsPath, $"*-{operationId}.json").Any())
        {
            throw new InvalidOperationException($"Peer operation {operationId} already exists.");
        }

        var currentHead = await ReadPeerHeadAsync(localDevice.DeviceId, cancellationToken).ConfigureAwait(false);
        var sequence = (currentHead?.HeadSequenceNumber ?? 0) + 1;
        var record = new PeerOperationRecord(
            operationId,
            localDevice.DeviceId,
            localDevice.DisplayName,
            sequence,
            DateTimeOffset.UtcNow,
            draft.Kind,
            draft.VersionId,
            draft.SourcePath,
            draft.ContentSignature,
            draft.Metadata ?? new Dictionary<string, string>());
        var operationPath = Path.Combine(operationsPath, $"{sequence:D20}-{operationId}.json");
        AtomicWrite(operationPath, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions), overwrite: false);
        var head = new PeerHeadRecord(
            localDevice.DeviceId,
            localDevice.DisplayName,
            sequence,
            operationId,
            record.CreatedAtUtc);
        AtomicWrite(HeadPath(localDevice.DeviceId), JsonSerializer.SerializeToUtf8Bytes(head, JsonOptions), overwrite: true);
        return record;
    }

    public async Task<PeerHeadRecord?> ReadPeerHeadAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var path = HeadPath(deviceId);
        return File.Exists(path)
            ? await ReadJsonAsync<PeerHeadRecord>(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<PeerHeadRecord>> ListPeerHeadsAsync(CancellationToken cancellationToken = default)
    {
        var peersPath = PeersPath();
        if (!Directory.Exists(peersPath))
        {
            return [];
        }

        var heads = new List<PeerHeadRecord>();
        foreach (var path in Directory.EnumerateFiles(peersPath, "head.json", SearchOption.AllDirectories))
        {
            heads.Add(await ReadJsonAsync<PeerHeadRecord>(path, cancellationToken).ConfigureAwait(false));
        }

        return heads
            .OrderBy(head => head.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<PeerOperationRecord>> ReadOperationsAfterAsync(
        string deviceId,
        long afterSequenceNumber,
        CancellationToken cancellationToken = default)
    {
        var operationsPath = OperationsPath(deviceId);
        if (!Directory.Exists(operationsPath))
        {
            return [];
        }

        var operations = new List<PeerOperationRecord>();
        foreach (var path in Directory.EnumerateFiles(operationsPath, "*.json"))
        {
            var operation = await ReadJsonAsync<PeerOperationRecord>(path, cancellationToken).ConfigureAwait(false);
            if (operation.SequenceNumber > afterSequenceNumber)
            {
                operations.Add(operation);
            }
        }

        return operations
            .OrderBy(operation => operation.SequenceNumber)
            .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ToArray();
    }

    public Task SaveCursorAsync(PeerCursorRecord cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var path = CursorPath(cursor.PeerDeviceId);
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions), overwrite: true);
        return Task.CompletedTask;
    }

    public async Task<PeerCursorRecord?> ReadCursorAsync(
        string peerDeviceId,
        CancellationToken cancellationToken = default)
    {
        var path = CursorPath(peerDeviceId);
        return File.Exists(path)
            ? await ReadJsonAsync<PeerCursorRecord>(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<PeerCursorRecord>> ListCursorsAsync(CancellationToken cancellationToken = default)
    {
        var cursorsPath = CursorsPath();
        if (!Directory.Exists(cursorsPath))
        {
            return [];
        }

        var cursors = new List<PeerCursorRecord>();
        foreach (var path in Directory.EnumerateFiles(cursorsPath, "*.json"))
        {
            cursors.Add(await ReadJsonAsync<PeerCursorRecord>(path, cancellationToken).ConfigureAwait(false));
        }

        return cursors
            .OrderBy(cursor => cursor.PeerDeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Sync metadata could not be read: {path}");
    }

    private string PeersPath()
    {
        return Path.Combine(repositoryPath, "sync", "peers");
    }

    private string OperationsPath(string deviceId)
    {
        return Path.Combine(PeersPath(), SafeName(deviceId), "operations");
    }

    private string HeadPath(string deviceId)
    {
        return Path.Combine(PeersPath(), SafeName(deviceId), "head.json");
    }

    private string CursorsPath()
    {
        return Path.Combine(repositoryPath, "sync", "cursors");
    }

    private string CursorPath(string peerDeviceId)
    {
        return Path.Combine(CursorsPath(), $"{SafeName(peerDeviceId)}.json");
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
