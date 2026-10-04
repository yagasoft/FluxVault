using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.App.Services;

public sealed record PendingBackupOperation(Guid RepositoryId, Guid OperationId, long Revision)
{
    internal void Validate()
    {
        if (RepositoryId == Guid.Empty || OperationId == Guid.Empty || Revision <= 0)
            throw new InvalidDataException("The pending backup identity or revision is invalid.");
    }
}

public interface IBackupOperationStore
{
    PendingBackupOperation? Read();
    void Reserve(PendingBackupOperation operation);
    void Clear(PendingBackupOperation operation);
}

public sealed class FileBackupOperationStore(string path) : IBackupOperationStore
{
    private readonly string recordPath = Path.GetFullPath(path);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 };
    public FileBackupOperationStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxVault", "pending-backup.json")) { }
    public PendingBackupOperation? Read()
    {
        using var gate = OpenGate();
        return ReadRecord();
    }

    public void Reserve(PendingBackupOperation operation)
    {
        operation.Validate();
        using var gate = OpenGate();
        if (ReadRecord() is not null) throw new InvalidOperationException("A backup outcome is still pending. Check it before requesting another backup.");
        // Dispatch is allowed only after this record is written and flushed. A partial
        // or unreadable record after interruption is retained and blocks another backup.
        using var output = new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, operation, JsonOptions);
        output.Flush(true);
    }

    public void Clear(PendingBackupOperation operation)
    {
        operation.Validate();
        using var gate = OpenGate();
        var pending = ReadRecord();
        if (pending is null) return;
        if (pending != operation) throw new InvalidOperationException("The pending backup changed in another session. Reload its outcome before continuing.");
        File.Delete(recordPath);
    }

    private FileStream OpenGate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        // LocalAppData is shared by sessions for the same Windows user. Hold this
        // file exclusively across read/create/conditional-delete, without a global mutex.
        return new(recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private PendingBackupOperation? ReadRecord()
    {
        FileStream input;
        try { input = new(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            if (input.Length is <= 0 or > 4096) throw new InvalidDataException("The pending backup record is empty or exceeds its bound.");
            try
            {
                using var document = JsonDocument.Parse(input, new() { MaxDepth = 4 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The pending backup record is invalid.");
                var properties = document.RootElement.EnumerateObject().ToArray();
                if (properties.Length != 3 || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 3)
                    throw new InvalidDataException("The pending backup record has missing or duplicate fields.");
                var operation = document.RootElement.Deserialize<PendingBackupOperation>(JsonOptions)
                    ?? throw new InvalidDataException("The pending backup record is missing.");
                operation.Validate();
                return operation;
            }
            catch (JsonException exception) { throw new InvalidDataException("The pending backup record could not be read. It has been preserved.", exception); }
        }
    }
}

internal sealed class MemoryBackupOperationStore : IBackupOperationStore
{
    private PendingBackupOperation? pending;
    public PendingBackupOperation? Read() => pending;
    public void Reserve(PendingBackupOperation operation)
    {
        operation.Validate();
        if (pending is not null) throw new InvalidOperationException("A backup is still pending.");
        pending = operation;
    }
    public void Clear(PendingBackupOperation operation)
    {
        if (pending is not null && pending != operation) throw new InvalidOperationException("The pending backup changed.");
        pending = null;
    }
}
