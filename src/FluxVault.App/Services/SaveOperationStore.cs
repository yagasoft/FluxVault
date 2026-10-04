using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.App.Services;

public sealed record PendingProtectionSave(Guid RepositoryId, Guid OperationId, long Revision,
    FluxVaultConfiguration Configuration, bool PurgeRemovedSelections,
    IReadOnlyList<RepositoryPurgeScope> RemovedSelections, IReadOnlyList<RepositoryPurgeScope> PreservedSelections)
{
    internal void Validate()
    {
        if (RepositoryId == Guid.Empty || OperationId == Guid.Empty || Revision <= 0 || Revision == long.MaxValue ||
            Configuration is null || string.IsNullOrWhiteSpace(Configuration.RepositoryPath) || RemovedSelections is null || PreservedSelections is null)
            throw new InvalidDataException("The pending protection save has invalid or missing fields. It has been preserved.");
    }
    internal FluxVaultIpcRequest Request => FluxVaultIpcRequest.SaveConfiguration(Configuration,
        purgeRemovedSelections: PurgeRemovedSelections, removedSelections: RemovedSelections, preservedSelections: PreservedSelections) with
        { VaultId = new VaultId(RepositoryId), OperationId = OperationId, ExpectedVaultRevision = Revision };
    internal PendingProtectionSave Freeze()
    {
        Validate();
        var request = JsonSerializer.SerializeToUtf8Bytes(Request, FileProtectionSaveOperationStore.JsonOptions);
        FileProtectionSaveOperationStore.ValidateJson(request);
        return JsonSerializer.Deserialize<PendingProtectionSave>(FileProtectionSaveOperationStore.Encode(this), FileProtectionSaveOperationStore.JsonOptions)!;
    }
}

public interface IProtectionSaveOperationStore
{
    PendingProtectionSave? Read();
    void Reserve(PendingProtectionSave save);
    void Clear(PendingProtectionSave save);
}

public sealed class FileProtectionSaveOperationStore(string path) : IProtectionSaveOperationStore
{
    private readonly string recordPath = Path.GetFullPath(path);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    private const int MaximumBytes = 1024 * 1024;
    public FileProtectionSaveOperationStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxVault", "pending-protection-save.json")) { }
    public PendingProtectionSave? Read() { using var gate = OpenGate(); return ReadRecord(); }
    public void Reserve(PendingProtectionSave save)
    {
        var bytes = Encode(save.Freeze()); // Validate the complete frozen wire snapshot before creating a record.
        using var gate = OpenGate();
        if (ReadRecord() is not null) throw new InvalidOperationException("A protection save outcome is still pending.");
        using var output = new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }
    public void Clear(PendingProtectionSave save)
    {
        var expected = Encode(save);
        using var gate = OpenGate();
        var pending = ReadRecord();
        if (pending is null) return;
        if (!expected.AsSpan().SequenceEqual(Encode(pending)))
            throw new InvalidOperationException("The pending protection save changed in another session. It has been preserved.");
        File.Delete(recordPath);
    }
    private FileStream OpenGate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        return new(recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private PendingProtectionSave? ReadRecord()
    {
        FileStream input;
        try { input = new(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("The pending save exceeds its bound or is empty.");
            var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            try
            {
                ValidateJson(bytes);
                using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 7)
                    throw new InvalidDataException("The pending save has missing fields. It has been preserved.");
                var pending = document.RootElement.Deserialize<PendingProtectionSave>(JsonOptions)!;
                pending.Validate();
                return pending.Freeze();
            }
            catch (JsonException exception) { throw new InvalidDataException("The pending save could not be read. It has been preserved.", exception); }
        }
    }
    internal static byte[] Encode(PendingProtectionSave save)
    {
        save.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(save, JsonOptions);
        ValidateJson(bytes);
        return bytes;
    }
    internal static void ValidateJson(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("The pending save exceeds its bound or is empty.");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The pending save contains duplicate fields. It has been preserved.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}

internal sealed class MemoryProtectionSaveOperationStore : IProtectionSaveOperationStore
{
    private PendingProtectionSave? pending;
    public PendingProtectionSave? Read() => pending;
    public void Reserve(PendingProtectionSave save)
    {
        if (pending is not null) throw new InvalidOperationException("A protection save is still pending.");
        pending = save.Freeze();
    }
    public void Clear(PendingProtectionSave save)
    {
        if (pending is not null && !FileProtectionSaveOperationStore.Encode(pending).AsSpan().SequenceEqual(FileProtectionSaveOperationStore.Encode(save)))
            throw new InvalidOperationException("The pending protection save changed.");
        pending = null;
    }
}
